using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        // Separate gate for the cheap detection phase so it is never blocked by an in-flight usage fetch.
        private readonly SemaphoreSlim _detectGate = new(1, 1);
        private readonly object _recentLock = new();
        private readonly List<ProviderId> _recentProviders = new();
        private Timer? _timer;
        // Synara persists provider switches through a 300 ms-debounced localStorage writer, and Chromium
        // then flushes that to its on-disk LevelDB on its own (variable, sometimes >1 s) cadence. The
        // file watcher catches most flushes, but FS events can coalesce or fire on a partially-written
        // log tail, and the generic 500 ms poll is too slow to feel instant. This dedicated low-latency
        // poll re-reads Synara's selection while it is the foreground app, so a switch reflects within
        // one interval of the data actually landing on disk regardless of FS-event timing.
        private Timer? _synaraPollTimer;
        // Synara's composer store writes through a 300 ms debounce. Filesystem events still publish as
        // soon as Chromium flushes, while this poll is only a fallback for missed/coalesced events.
        private static readonly TimeSpan SynaraPollInterval = TimeSpan.FromMilliseconds(100);
        // Wait-for-stable retry budget. The first read after an FS event may see a partially-written
        // log tail — we re-read a few times with ~8 ms gaps and publish the first value that repeats,
        // which is the strongest "Chromium is done" signal we can get without modifying Synara.
        private const int SynaraStableMaxAttempts = 4;
        private static readonly TimeSpan SynaraStableDelay = TimeSpan.FromMilliseconds(8);
        private ProviderId? _lastActive;
        private ProviderId? _lastLogged;
        private ProviderId? _synaraHoldProvider;
        private DateTime _synaraHoldUntilUtc = DateTime.MinValue;
        private int _synaraSwitchHandling;
        // Instant UI-Automation read. The composer button's accessible name changes the moment the user
        // picks. It is only authoritative when the name includes the host provider ("Provider · Model").
        // Current unlabelled builds expose just the model, so they fall through to the local state reader
        // instead of guessing from model names.
        private string? _lastUiaModelName;
        // While an instant UIA read is held, the still-lagging disk read can show the PRE-SWITCH
        // provider and must not flicker the published one back. During the hold the disk path is only
        // accepted when it agrees with the held provider (enrichment); any other provider is treated as
        // stale and ignored. After the window the disk is authoritative again (UIA may have stopped
        // publishing if Synara lost foreground).
        private ProviderId? _uiaProvider;
        private DateTime _uiaHoldUntilUtc = DateTime.MinValue;
        private static readonly TimeSpan UiaReconcileWindow = TimeSpan.FromSeconds(10);
        private bool? _lastHasDetectedTool;
        private DateTime _lastPresenceProbeAt = DateTime.MinValue;

        public UsageService Service => _service;
        public ProviderId? ActiveProvider => _lastActive;

        /// <summary>
        /// The provider the taskbar widget should display: the active provider when its widget is enabled,
        /// otherwise the first enabled-and-available provider (most recently active first, then enum order).
        /// Null when no provider qualifies, in which case the widget hides instead of falling back to a
        /// hidden default. Fixes the "widget disappears when Codex is disabled / only Cursor enabled" bug
        /// (the old code hard-coded <see cref="ProviderId.Codex"/> as the fallback). See issue #7.
        /// </summary>
        public ProviderId? WidgetDisplayProvider
        {
            get
            {
                if (_lastActive is { } active && WidgetSettingsService.IsProviderVisible(active))
                    return active;
                foreach (var p in RecentProviders)
                    if (WidgetSettingsService.IsProviderVisible(p) && IsProviderAvailable(p))
                        return p;
                foreach (ProviderId p in Enum.GetValues<ProviderId>())
                    if (WidgetSettingsService.IsProviderVisible(p) && IsProviderAvailable(p))
                        return p;
                return null;
            }
        }

        // A provider can back the widget only if it is actually installed or has been configured — so we
        // never fall back to an enabled-by-default provider the user doesn't even have.
        private static bool IsProviderAvailable(ProviderId provider) =>
            ProviderInstallDetector.IsInstalled(provider) || ProviderDiscoveryService.IsConfigured(provider);
        /// <summary>
        /// When the active provider was resolved through the Synara host app, the active thread's
        /// selection (inner provider + model); null otherwise. The taskbar widget reads this to badge the
        /// provider icon with the Synara mark.
        /// </summary>
        public ActiveApp.SynaraStateReader.SynaraSelection? ActiveSynaraHost { get; private set; }
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
            // Warm WMI off-thread so the first terminal detection isn't blocked by COM cold start.
            _ = Task.Run(() => _detector.Prewarm());
            _detector.OpenCodeModelStateChanged += OnOpenCodeModelStateChanged;
            _detector.StartOpenCodeModelStateWatcher();
            _detector.SynaraStateChanged += OnSynaraStateChanged;
            _detector.StartSynaraStateWatcher();
            // Fast tick for snappy active-app switching; usage fetches respect per-result cache TTL (60s ok, 5m on 429).
            _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
            // Steady low-latency Synara poll. Cheap when Synara is not foreground / unchanged: the fast
            // detect short-circuits on the foreground process name and the LevelDB reader serves cached
            // state until the log grows, so this only does real work the instant a switch hits disk.
            _synaraPollTimer = new Timer(_ => PollSynaraSwitch(), null, SynaraPollInterval, SynaraPollInterval);
        }

        private void OnOpenCodeModelStateChanged() => _ = HandleOpenCodeModelSwitchAsync();

        // Synara fires this the instant its localStorage LevelDB changes (provider switch / thread
        // navigation). Resolve and publish immediately; the steady poll then covers any slower or
        // partially-written follow-up flush from Chromium.
        private void OnSynaraStateChanged() => TryHandleSynaraSwitch(waitForStable: true);

        // Steady-cadence companion to the file watcher. Each tick first tries the instant UI read: on
        // labelled Synara builds the composer button's accessible name carries the authoritative HOST
        // provider for every provider (Codex / Claude / Cursor / Grok / OpenCode / OpenCode Go), so when
        // it publishes the disk read is skipped this tick. Otherwise (current unlabelled build,
        // ambiguous model, or Synara not foreground) the disk path resolves the authoritative provider.
        private void PollSynaraSwitch()
        {
            try
            {
                if (TryHandleSynaraUiaSwitch())
                    return;
                TryHandleSynaraSwitch(waitForStable: false);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"[synara] poll failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Instant path: read Synara's live composer model-button name (UI Automation) and, when it
        /// identifies a host provider, publish immediately. On labelled builds the name is
        /// "{Host} · {Model}" so the host is unambiguous and instant for EVERY provider — including
        /// Cursor / OpenCode (and OpenCode Go vs Zen), which proxy the same model names as the native
        /// brands. Returns true when it published this tick so the caller skips the slower disk read.
        /// Unlabelled builds expose only the model name, so they return false and the authoritative state
        /// reader resolves the provider without guessing.
        /// </summary>
        private bool TryHandleSynaraUiaSwitch()
        {
            var buttonName = _detector.TryReadForegroundSynaraModel();
            if (string.IsNullOrEmpty(buttonName))
            {
                _lastUiaModelName = null;
                return false;
            }

            if (buttonName == _lastUiaModelName)
                return false;
            _lastUiaModelName = buttonName;

            if (SynaraModelClassifier.Classify(buttonName) is not { } classification)
                return false; // Bare model on an unlabelled build; state reader is authoritative.

            if (Interlocked.Exchange(ref _synaraSwitchHandling, 1) == 1)
                return false;
            try
            {
                var sw = Stopwatch.StartNew();
                var resolved = classification.Provider;
                var host = new SynaraStateReader.SynaraSelection(
                    resolved, SynaraProviderLiteral(resolved), classification.ModelDisplayName, ThreadTitle: null,
                    Host: _detector.TryGetForegroundHost() ?? HostApp.Synara);

                // Hold the authoritative UIA provider so the still-lagging disk read can't flicker it
                // back before localStorage catches up. The disk path only enriches (model id / thread
                // title) when it agrees with this provider; a disagreeing disk read is treated as stale.
                _uiaProvider = resolved;
                _uiaHoldUntilUtc = DateTime.UtcNow + UiaReconcileWindow;

                return PublishSynaraSelection(host, sw, "uia", attempts: 1);
            }
            finally
            {
                Interlocked.Exchange(ref _synaraSwitchHandling, 0);
            }
        }

        private void TryHandleSynaraSwitch(bool waitForStable)
        {
            if (Interlocked.Exchange(ref _synaraSwitchHandling, 1) == 1)
                return;

            var sw = Stopwatch.StartNew();
            try
            {
                // The live on-screen model (read via UI Automation when Synara is foreground) is only used
                // to disambiguate WHICH stored selection is active — so a stale focused-thread draft can't
                // mis-resolve the provider (e.g. report OpenCode while the composer is on Cursor). The
                // provider itself always comes from Synara's authoritative localStorage selection.
                var onScreenModel = _detector.TryReadForegroundSynaraModel();

                // Detection runs outside the coordinator gate (and outside the detector's full Detect lock)
                // so this path is never queued behind TickAsync's WMI scan or a thread-pool hop.
                // Filesystem events can arrive while Chromium is still appending a record, so only that
                // path does a short stable-read retry. The steady poll is a fallback and reads once.
                var host = waitForStable
                    ? DetectSynaraSelectionStable(onScreenModel, out var stableAttempts)
                    : DetectSynaraSelectionOnce(onScreenModel, out stableAttempts);
                if (host is null)
                    return;

                // While an instant UIA read is held, the disk read can still be lagging on the
                // PRE-SWITCH provider. Accept it only when it agrees with the held provider (it's the
                // truth catching up and can enrich the model id / thread title); ignore it when it
                // differs (stale, would flicker back). After the window the disk is authoritative again.
                if (_uiaProvider is ProviderId held
                    && DateTime.UtcNow < _uiaHoldUntilUtc
                    && host.Provider != held)
                {
                    return;
                }
                ClearUiaHold();

                PublishSynaraSelection(host, sw, "disk", stableAttempts);
            }
            finally
            {
                Interlocked.Exchange(ref _synaraSwitchHandling, 0);
            }
        }

        /// <summary>
        /// Shared publish core for both the UIA and disk Synara paths. Applies the selection under the
        /// detect gate, raises provider/state events only on a real change, and kicks a usage refresh.
        /// Returns true when it published. The caller owns the <see cref="_synaraSwitchHandling"/> guard.
        /// </summary>
        private bool PublishSynaraSelection(
            SynaraStateReader.SynaraSelection host, Stopwatch sw, string source, int attempts)
        {
            var provider = host.Provider;
            ProviderId? refreshTarget = null;
            if (!TryEnterDetectGate())
                return false;

            try
            {
                var previousHost = ActiveSynaraHost;
                ActiveSynaraHost = host;
                HoldSynaraProvider(provider);
                var previous = _lastActive;
                var providerChanged = previous != provider;
                var hostChanged = !SameSynaraSelection(previousHost, host);
                _lastActive = provider;
                if (providerChanged)
                    PromoteRecentProvider(provider);

                if (_lastHasDetectedTool != true)
                {
                    _lastHasDetectedTool = true;
                    ActiveToolPresenceChanged?.Invoke(true);
                }

                // Synara fires on every localStorage write (incl. composer keystrokes). Only act when the
                // provider/model/thread selection actually changed — otherwise it's already on screen.
                if (!providerChanged && !hostChanged)
                    return false;

                Diagnostics.Log.Debug($"[synara] switch detected source={source} provider={provider} model={host.Model ?? "n/a"} attempts={attempts} detect={sw.Elapsed.TotalMilliseconds:0.0}ms");
                if (providerChanged)
                    ActiveProviderChanged?.Invoke(provider);
                PublishImmediateState(provider);
                Diagnostics.Log.Debug($"[synara] immediate state published source={source} provider={provider} total={sw.Elapsed.TotalMilliseconds:0.0}ms");
                refreshTarget = provider;
            }
            finally
            {
                _detectGate.Release();
            }

            if (refreshTarget is ProviderId target)
                _ = RefreshSynaraUsageAsync(target);
            return refreshTarget is not null;
        }

        /// <summary>Synara's provider literal for a UIA-labelled provider (mirrors <see cref="SynaraStateReader.MapProvider"/>).</summary>
        private static string SynaraProviderLiteral(ProviderId provider) => provider switch
        {
            ProviderId.Codex => "codex",
            ProviderId.Claude => "claudeagent",
            ProviderId.Cursor => "cursor",
            ProviderId.Grok => "grok",
            // Synara's provider literal is "opencode" for both the Zen/BYOK and the Go (subscription)
            // backends — the Go/Zen split lives in the model id prefix ("opencode-go/..."), not the
            // literal — so the UIA-published literal matches what the disk reader emits.
            ProviderId.OpenCode => "opencode",
            ProviderId.OpenCodeGo => "opencode",
            _ => provider.ToString().ToLowerInvariant(),
        };

        private SynaraStateReader.SynaraSelection? DetectSynaraSelectionOnce(string? onScreenModel, out int attempts)
        {
            attempts = 1;
            return _detector.DetectSynaraSelectionFast(onScreenModel);
        }

        // Returns the active Synara selection once it has been observed to be stable for one read, or
        // immediately if the read equals the last published selection. Up to SynaraStableMaxAttempts
        // reads with SynaraStableDelay between them; the first value that repeats is the winner.
        // This is the cheapest way to bridge Chromium's batched-flush window without modifying Synara.
        private SynaraStateReader.SynaraSelection? DetectSynaraSelectionStable(string? onScreenModel, out int attempts)
        {
            attempts = 0;
            SynaraStateReader.SynaraSelection? last = null;
            for (var i = 0; i < SynaraStableMaxAttempts; i++)
            {
                attempts = i + 1;
                var current = _detector.DetectSynaraSelectionFast(onScreenModel);
                if (current is null)
                    return null;

                if (last is not null && SameSynaraSelection(last, current))
                {
                    if (i > 0)
                        Diagnostics.Log.Debug($"[synara] stable after {attempts} reads ({SynaraStableDelay.TotalMilliseconds * i:0}ms)");
                    return current;
                }

                last = current;
                if (i < SynaraStableMaxAttempts - 1)
                    Thread.Sleep(SynaraStableDelay);
            }
            return last;
        }

        private async Task RefreshSynaraUsageAsync(ProviderId targetProvider)
        {
            try
            {
                var fresh = await _service.FetchAsync(targetProvider, force: true).ConfigureAwait(false);
                if (!await _gate.WaitAsync(0).ConfigureAwait(false))
                    return;
                try
                {
                    if (ActiveSynaraHost is null || _lastActive != targetProvider)
                        return;

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
                Diagnostics.Log.Warning(ex, "Synara switch refresh failed");
            }
        }

        private static bool SameSynaraSelection(
            SynaraStateReader.SynaraSelection? left,
            SynaraStateReader.SynaraSelection? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null)
                return false;

            // ThreadTitle is intentionally excluded: it's a cosmetic tooltip, and the tick (titled) vs the
            // poll (untitled) reading the same selection must not be seen as a change — that would
            // republish and re-fetch every tick.
            return left.Provider == right.Provider
                && left.Host == right.Host
                && string.Equals(left.ProviderLiteral, right.ProviderLiteral, StringComparison.Ordinal)
                && string.Equals(left.Model, right.Model, StringComparison.Ordinal);
        }

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
            else if (_service.TryGetLastSuccessfulLiveResult(target, out var lastSuccess))
                snapshot = lastSuccess;
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

        /// <summary>Fetch providers needed for the dashboard and report each result as it arrives.</summary>
        public async Task FetchAllProgressiveAsync(
            bool force,
            Action<UsageResult> onResult,
            CancellationToken ct = default)
        {
            var active = ActiveProvider;
            var tasks = _service.All
                .Where(p => force || ProviderDiscoveryService.ShouldFetch(p.Id, active))
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
            // Phase 1 — detection. Run the slow foreground/WMI scan outside the gate so Synara's
            // file-watcher path can publish provider switches while a tick is mid-detect.
            ProviderId target;
            ProviderId? detected;
            SynaraStateReader.SynaraSelection? detectedSynaraHost;
            try
            {
                detected = _detector.Detect();
                detectedSynaraHost = _detector.ActiveSynaraHost;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error(ex, "Coordinator detect failed");
                return;
            }

            if (!await _detectGate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                // Mirror the Synara switch path's guard: while an instant UIA read is held, a still-lagging
                // Synara disk detect can show the pre-switch provider. Only suppress a DISAGREEING Synara
                // disk read (keep the held UIA provider so it doesn't flicker back); when the disk agrees
                // (or the hold has expired) let it through and clear the hold. A non-Synara detect (the
                // user switched to another app) is never suppressed — the new foreground app wins.
                if (_uiaProvider is ProviderId uiaHeld
                    && DateTime.UtcNow < _uiaHoldUntilUtc
                    && detectedSynaraHost is { } laggingHost
                    && laggingHost.Provider != uiaHeld)
                {
                    detectedSynaraHost = ActiveSynaraHost;
                    detected = uiaHeld;
                }
                else if (_uiaProvider is not null && DateTime.UtcNow >= _uiaHoldUntilUtc)
                {
                    ClearUiaHold();
                }

                if (detectedSynaraHost is { } synaraHost)
                {
                    ActiveSynaraHost = synaraHost;
                    HoldSynaraProvider(synaraHost.Provider);
                }
                else if (ShouldHoldSynaraProvider(detected))
                {
                    detected = _synaraHoldProvider;
                }
                else
                {
                    ActiveSynaraHost = null;
                    ClearSynaraHold();
                }

                var hasDetectedTool = detected != null || ShouldAssumeToolStillRunning() || ProbeToolPresence();
                if (!hasDetectedTool)
                {
                    if (_lastHasDetectedTool != false)
                    {
                        _lastHasDetectedTool = false;
                        _lastActive = null;
                        _lastLogged = null;
                        ActiveSynaraHost = null;
                        ClearSynaraHold(force: true);
                        ActiveToolPresenceChanged?.Invoke(false);
                    }
                    return;
                }

                ProviderId? previousActive = _lastActive;
                if (detected is ProviderId p)
                {
                    _lastActive = p;
                    PromoteRecentProvider(p);
                    if (previousActive != p)
                    {
                        ActiveProviderChanged?.Invoke(p);
                        PublishImmediateState(p);
                    }
                }

                if (detected is null && _lastActive is null)
                    return;

                if (_lastHasDetectedTool != true)
                {
                    _lastHasDetectedTool = true;
                    ActiveToolPresenceChanged?.Invoke(true);
                }

                // Last-active fallback: nothing detected and never had one -> show the first enabled
                // provider (never a hidden default), but do not make the fallback sticky as the active one.
                target = _lastActive ?? WidgetDisplayProvider ?? ProviderId.Codex;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error(ex, "Coordinator detect failed");
                return;
            }
            finally
            {
                _detectGate.Release();
            }

            // Phase 2 — usage fetch (may hit the network). Gated separately and skipped if a fetch is
            // already running, so a slow fetch can never stall detection of the next provider switch.
            if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                var result = await _service.FetchAsync(target, force).ConfigureAwait(false);

                // The active provider may have changed while we awaited the network; if so, drop this
                // stale result and let the next tick fetch the current target.
                if (target != (_lastActive ?? WidgetDisplayProvider ?? ProviderId.Codex))
                    return;

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
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error(ex, "Coordinator fetch failed");
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

        // Brief spin before giving up — tick only holds the gate for state apply, not WMI.
        private bool TryEnterDetectGate()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (_detectGate.Wait(0))
                    return true;
                Thread.SpinWait(50);
            }

            return false;
        }

        private void HoldSynaraProvider(ProviderId provider)
        {
            _synaraHoldProvider = provider;
            _synaraHoldUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        }

        private void ClearUiaHold()
        {
            _uiaProvider = null;
            _uiaHoldUntilUtc = DateTime.MinValue;
        }

        // Hold the last Synara provider only while Synara is STILL the foreground app — it bridges
        // Synara's own transient null reads (Chromium a11y / localStorage hiccups). The moment the user
        // leaves Synara for another app, the hold must release so the widget doesn't sit on the stale
        // Synara provider for the full window (was a ~15s "stuck on Synara" after switching away).
        private bool ShouldHoldSynaraProvider(ProviderId? detected)
            => _synaraHoldProvider is ProviderId held
            && _lastActive == held
            && DateTime.UtcNow < _synaraHoldUntilUtc
            && detected is null
            && _detector.TryGetForegroundSynaraWindow() != IntPtr.Zero;

        private void ClearSynaraHold(bool force = false)
        {
            if (!force && DateTime.UtcNow < _synaraHoldUntilUtc)
                return;

            _synaraHoldProvider = null;
            _synaraHoldUntilUtc = DateTime.MinValue;
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
