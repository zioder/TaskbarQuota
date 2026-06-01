using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.ActiveApp;
using TaskbarQuota.Usage;

namespace TaskbarQuota
{
    /// <summary>
    /// Drives the whole app: on a timer it detects the active AI tool (falling back to the last
    /// active one), fetches that provider's usage, and raises <see cref="StateChanged"/> so the
    /// widget and main window can update. Single shared instance for the process.
    /// </summary>
    public sealed class UsageCoordinator
    {
        public static UsageCoordinator Instance { get; } = new();

        private readonly ActiveAppDetector _detector = new();
        private readonly UsageService _service = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _recentLock = new();
        private readonly List<ProviderId> _recentProviders = new();
        private Timer? _timer;
        private ProviderId? _lastActive;
        private ProviderId? _lastLogged;
        private bool? _lastHasDetectedTool;
        private DateTime _lastPresenceProbeAt = DateTime.MinValue;

        public UsageService Service => _service;
        public ProviderId? ActiveProvider => _lastActive;
        /// <summary>Last usage snapshot pushed to listeners; used to hydrate the taskbar widget if it was created late.</summary>
        public UsageResult? LastState { get; private set; }
        public bool IsActiveToolPresent => _lastHasDetectedTool ?? _detector.HasAnyKnownToolRunning();
        public IReadOnlyList<ProviderId> RecentProviders
        {
            get
            {
                lock (_recentLock)
                    return _recentProviders.ToArray();
            }
        }

        public event Action<UsageResult>? StateChanged;
        public event Action<bool>? ActiveToolPresenceChanged;
        public event Action<ProviderId?>? ActiveProviderChanged;

        public void Start()
        {
            if (_timer != null) return;
            _detector.OpenCodeModelStateChanged += OnOpenCodeModelStateChanged;
            _detector.StartOpenCodeModelStateWatcher();
            // Fast tick for snappy active-app switching; usage fetches respect per-result cache TTL (60s ok, 5m on 429).
            _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
        }

        private void OnOpenCodeModelStateChanged() => _ = HandleOpenCodeModelSwitchAsync();

        private async Task HandleOpenCodeModelSwitchAsync()
        {
            var foreground = _detector.Detect();
            var modelProvider = ActiveAppDetector.DetectOpenCodeProviderFromModelState();

            if (!ShouldReactToOpenCodeModelChange(foreground))
            {
                if (modelProvider is ProviderId backgroundProvider)
                    await RefreshProviderCacheSilentlyAsync(backgroundProvider).ConfigureAwait(false);
                return;
            }

            var target = modelProvider ?? foreground!.Value;
            if (!IsOpenCodeProvider(target))
                return;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ShouldReactToOpenCodeModelChange(_detector.Detect()))
                    return;

                var previous = _lastActive;
                _lastActive = target;
                PromoteRecentProvider(target);

                if (previous != target)
                    ActiveProviderChanged?.Invoke(target);

                PublishImmediateState(target);

                if (_lastHasDetectedTool != true)
                {
                    _lastHasDetectedTool = true;
                    ActiveToolPresenceChanged?.Invoke(true);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error(ex, "OpenCode model switch failed");
                return;
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                var fresh = await _service.FetchAsync(target, force: true).ConfigureAwait(false);
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ShouldReactToOpenCodeModelChange(_detector.Detect()) || _lastActive != target)
                        return;

                    if (target != _lastLogged)
                    {
                        _lastLogged = target;
                        if (fresh.Ok && fresh.Fetch is { } f)
                            Diagnostics.Log.Information($"Switched to {target} (opencode model) session={f.Usage.Primary.UsedPercent:0}% weekly={f.Usage.Secondary?.UsedPercent ?? -1:0}% plan={f.Usage.LoginMethod}");
                        else
                            Diagnostics.Log.Warning($"Switched to {target} (opencode model) FAILED: {fresh.Error}");
                    }

                    LastState = fresh;
                    StateChanged?.Invoke(fresh);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error(ex, "OpenCode model switch refresh failed");
            }
        }

        internal static bool ShouldReactToOpenCodeModelChange(ProviderId? foregroundProvider)
            => foregroundProvider is ProviderId.OpenCode or ProviderId.OpenCodeGo;

        internal static bool IsOpenCodeProvider(ProviderId provider)
            => provider is ProviderId.OpenCode or ProviderId.OpenCodeGo;

        private async Task RefreshProviderCacheSilentlyAsync(ProviderId provider)
        {
            try
            {
                await _service.FetchAsync(provider, force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, $"Background refresh for {provider} failed");
            }
        }

        private void PublishImmediateState(ProviderId target)
        {
            UsageResult snapshot;
            if (_service.TryGetCached(target, out var cached))
                snapshot = cached;
            else if (_service.Get(target) is { } provider)
                snapshot = UsageResult.Pending(target, provider, "Loading...");
            else
                return;

            LastState = snapshot;
            StateChanged?.Invoke(snapshot);
        }

        /// <summary>Fetch all providers (cached) for the multi-provider view.</summary>
        public async Task<IReadOnlyList<UsageResult>> FetchAllAsync(bool force = false)
        {
            var tasks = _service.All
                .Select(p => Task.Run(() => _service.FetchAsync(p.Id, force)))
                .ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return SortByRecentActivity(results, RecentProviders, ActiveProvider);
        }

        /// <summary>Fetch all providers and report each result as soon as it arrives.</summary>
        public async Task FetchAllProgressiveAsync(
            bool force,
            Action<UsageResult> onResult,
            CancellationToken ct = default)
        {
            var tasks = _service.All
                .Select(p => Task.Run(() => _service.FetchAsync(p.Id, force, ct), ct))
                .ToList();

            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completed);
                onResult(await completed.ConfigureAwait(false));
            }
        }

        /// <summary>Fetch every registered provider once so the cache is warm and each is verified.</summary>
        public async Task WarmUpAsync()
        {
            var tasks = _service.All
                .Select(provider => FetchWarmUpResultAsync(provider))
                .ToList();

            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completed);

                var r = await completed.ConfigureAwait(false);
                LogWarmUpResult(r);
            }
        }

        private async Task<UsageResult> FetchWarmUpResultAsync(IUsageProvider provider)
            => await _service.FetchAsync(provider.Id, force: true).ConfigureAwait(false);

        private static void LogWarmUpResult(UsageResult r)
        {
            if (r.Ok && r.Fetch is { } f)
                Diagnostics.Log.Information($"WarmUp {r.Id}: session={f.Usage.Primary.UsedPercent:0}% weekly={f.Usage.Secondary?.UsedPercent ?? -1:0}% plan={f.Usage.LoginMethod}");
            else
                Diagnostics.Log.Warning($"WarmUp {r.Id} FAILED: {r.Error}");
        }

        public async Task TickAsync(bool force = false)
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                var detected = _detector.Detect();
                var hasDetectedTool = detected != null || ShouldAssumeToolStillRunning() || ProbeToolPresence();
                if (!hasDetectedTool)
                {
                    if (_lastHasDetectedTool != false)
                    {
                        _lastHasDetectedTool = false;
                        _lastActive = null;
                        _lastLogged = null;
                        ActiveToolPresenceChanged?.Invoke(false);
                    }
                    return;
                }

                if (detected is ProviderId p)
                {
                    var previous = _lastActive;
                    _lastActive = p;
                    PromoteRecentProvider(p);
                    if (previous != p)
                        ActiveProviderChanged?.Invoke(p);
                }

                if (detected is null && _lastActive is null)
                    return;

                // Last-active fallback: nothing detected and never had one -> show Codex,
                // but do not make the fallback sticky as the active provider.
                var target = _lastActive ?? ProviderId.Codex;

                var result = await _service.FetchAsync(target, force).ConfigureAwait(false);
                // Only log when the active provider actually changes (1s ticks would otherwise spam).
                if (target != _lastLogged)
                {
                    _lastLogged = target;
                    if (result.Ok && result.Fetch is { } f)
                        Diagnostics.Log.Information($"Switched to {target} (detected={detected}) session={f.Usage.Primary.UsedPercent:0}% weekly={f.Usage.Secondary?.UsedPercent ?? -1:0}% plan={f.Usage.LoginMethod}");
                    else
                        Diagnostics.Log.Warning($"Switched to {target} (detected={detected}) FAILED: {result.Error}");
                }
                LastState = result;
                StateChanged?.Invoke(result);

                if (_lastHasDetectedTool != true)
                {
                    _lastHasDetectedTool = true;
                    ActiveToolPresenceChanged?.Invoke(true);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error(ex, "Coordinator tick failed");
            }
            finally
            {
                _gate.Release();
            }
        }

        private void PromoteRecentProvider(ProviderId provider)
        {
            lock (_recentLock)
            {
                _recentProviders.Remove(provider);
                _recentProviders.Insert(0, provider);
            }
        }

        private bool ShouldAssumeToolStillRunning()
            => _lastHasDetectedTool == true && DateTime.UtcNow - _lastPresenceProbeAt < TimeSpan.FromSeconds(15);

        private bool ProbeToolPresence()
        {
            _lastPresenceProbeAt = DateTime.UtcNow;
            return _detector.HasAnyKnownToolRunning();
        }

        internal static IReadOnlyList<UsageResult> SortByRecentActivity(
            IReadOnlyList<UsageResult> results,
            IReadOnlyList<ProviderId> recentProviders,
            ProviderId? active)
        {
            var originalIndex = results
                .Select((result, index) => (result.Id, index))
                .ToDictionary(x => x.Id, x => x.index);

            var recentIndex = recentProviders
                .Select((id, index) => (id, index))
                .ToDictionary(x => x.id, x => x.index);

            return results
                .OrderBy(r => active == r.Id ? 0 : 1)
                .ThenBy(r => recentIndex.TryGetValue(r.Id, out var index) ? index : int.MaxValue)
                .ThenBy(r => originalIndex[r.Id])
                .ToArray();
        }
    }
}
