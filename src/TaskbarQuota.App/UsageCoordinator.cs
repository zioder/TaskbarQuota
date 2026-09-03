using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.ActiveApp;
using TaskbarQuota.Interop;
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
        // Persist last-good snapshots so the taskbar widget renders real numbers right after a reboot.
        private readonly UsageService _service = new(UsageSnapshotStore.DefaultDirectory);
        private readonly SemaphoreSlim _gate = new(1, 1);
        // Separate gate for the cheap detection phase so it is never blocked by an in-flight usage fetch.
        private readonly SemaphoreSlim _detectGate = new(1, 1);
        private readonly object _recentLock = new();
        private readonly List<ProviderId> _recentProviders = new();
        // Providers with a pinned-tile refresh in flight, so the 5s health timer never stacks a second
        // fetch for one that is still running — a slow or offline endpoint would otherwise accumulate
        // concurrent requests and could publish an older snapshot out of order.
        private readonly HashSet<ProviderId> _widgetRefreshInFlight = new();
        private readonly object _openCodeModelStateLock = new();
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
        // A foreground process can briefly report a sibling provider while a GUI window is being raised.
        // Do not publish a one-sample provider switch: that makes the taskbar animate a foreign quota tile
        // in and back out even though the user never left the current app.
        private ProviderId? _pendingDetectedProvider;
        private int _pendingDetectedProviderSamples;
        private const int RequiredProviderSwitchSamples = 2;
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
        private ProviderSource _activeProviderSource = ProviderSource.Unknown;
        private ProviderId? _lastObservedOpenCodeProvider;
        private bool _hasObservedOpenCodeProvider;
        // Focus-follows-provider state, only consulted when WidgetSettingsService.HideWhenProviderUnfocused
        // is on. Starts true so the widget shows from launch and only ever hides after a detect that
        // actually saw an unrelated foreground app.
        private bool _providerForegroundActive = true;
        private DateTime _providerUnfocusedSinceUtc = DateTime.MinValue;
        // Grace period before the tile is dropped, counted from the first provider-free foreground. Kept
        // short: it only exists to swallow the momentary focusless gap while a window is being raised, and
        // anything longer reads as the widget lagging behind the window switch. Showing is always instant.
        // 120ms: enough to absorb the transient focus gap during Alt-Tab/window transitions (~80ms max),
        // short enough that the hide feels instant to the user rather than sluggish.
        private static readonly TimeSpan ProviderUnfocusHideDelay = TimeSpan.FromMilliseconds(120);
        // Re-check scheduled when a foreground switch starts the grace period, so the hide lands as soon as
        // it expires instead of waiting for the next 500 ms detect tick.
        private Timer? _unfocusHideTimer;
        // The focus state is written from the detect tick, the foreground hook and the grace timer — three
        // different threads.
        private readonly object _focusLock = new();

        public UsageService Service => _service;
        public ProviderId? ActiveProvider => _lastActive;
        public ProviderSource ActiveProviderSource => _activeProviderSource;

        /// <summary>
        /// The provider the taskbar widget should display when an active provider is known. A missing active
        /// provider returns null; the widget must not invent one from the installed-provider enum order.
        /// Pinned providers are handled separately by <see cref="WidgetDisplayProviders"/>.
        /// </summary>
        public ProviderId? WidgetDisplayProvider
        {
            get
            {
                if (_lastActive is not { } active)
                    return null;

                if (WidgetSettingsService.IsProviderVisible(active))
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

        /// <summary>
        /// Maximum number of quota tile slots allocated by the widget. The effective display cap is lower
        /// while the activity widget is enabled because the activity island occupies the same taskbar area.
        /// </summary>
        public const int MaxWidgetTiles = 3;

        /// <summary>Effective quota-tile cap: active + two pinned tiles normally, active + one pinned with activity.</summary>
        public static int MaxDisplayedWidgetTiles =>
            WidgetSettingsService.ShowAgentActivityInWidget ? 2 : MaxWidgetTiles;

        /// <summary>
        /// Every provider the taskbar widget should render as its own tile, left to right: the ACTIVE
        /// provider always first, then the pinned providers (most recently active first, then enum order).
        /// So with Claude pinned + Z.AI pinned and Codex active you get "Codex | Claude | Z.AI", and
        /// focusing Claude re-orders to "Claude | Z.AI" + whatever else is pinned — the active provider
        /// keeps the leading slot while the pinned tiles stay put behind it (issue #25).
        /// With no active provider this returns only pinned providers; with neither an active nor pinned
        /// provider it is empty, so the taskbar stays clear until detection selects a provider.
        /// </summary>
        public IReadOnlyList<ProviderId> WidgetDisplayProviders
            => ComputeWidgetDisplayProviders(
                _lastActive,
                IsActiveToolPresent && IsActiveTileAllowedByFocus,
                RecentProviders,
                Enum.GetValues<ProviderId>(),
                WidgetSettingsService.IsProviderPinned,
                WidgetSettingsService.IsProviderVisible,
                IsProviderAvailable,
                WidgetSettingsService.ShowAgentActivityInWidget);

        /// <summary>Pure, testable core of <see cref="WidgetDisplayProviders"/>.</summary>
        internal static IReadOnlyList<ProviderId> ComputeWidgetDisplayProviders(
            ProviderId? active,
            bool present,
            IReadOnlyList<ProviderId> recent,
            IReadOnlyList<ProviderId> ordered,
            Func<ProviderId, bool> isPinned,
            Func<ProviderId, bool> isVisible,
            Func<ProviderId, bool> isAvailable,
            bool activityWidgetEnabled = false)
        {
            var result = new List<ProviderId>();

            // The active provider leads even when it is itself pinned — it is the one the user is looking
            // at right now, so it gets the stable leftmost slot and the pinned tiles trail it.
            if (present && active is { } a && isVisible(a))
                result.Add(a);

            var recentIndex = new Dictionary<ProviderId, int>();
            for (int i = 0; i < recent.Count; i++)
                recentIndex.TryAdd(recent[i], i);

            var pinned = ordered
                .Where(p => isPinned(p) && isVisible(p) && isAvailable(p) && !result.Contains(p))
                .OrderBy(p => recentIndex.TryGetValue(p, out int index) ? index : int.MaxValue)
                .ToList();
            result.AddRange(pinned);

            int maxTiles = activityWidgetEnabled ? 2 : MaxWidgetTiles;
            if (result.Count > maxTiles)
                result.RemoveRange(maxTiles, result.Count - maxTiles);
            return result;
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

        /// <summary>
        /// Whether the active provider currently earns a tile. Always true unless the user opted into
        /// <see cref="WidgetSettingsService.HideWhenProviderUnfocused"/>, in which case it follows whether
        /// a provider app is actually in the foreground.
        /// </summary>
        public bool IsActiveTileAllowedByFocus
            => !WidgetSettingsService.HideWhenProviderUnfocused || _providerForegroundActive;

        /// <summary>
        /// Set by the taskbar layer to report that our own UI is on screen (the flyout). While it returns
        /// true the focus tracker holds its current state, so opening the flyout — which takes the
        /// foreground away from the provider app — never hides the widget the user is interacting with.
        /// </summary>
        public Func<bool>? IsOwnUiEngaged { get; set; }

        /// <summary>Raised when <see cref="IsActiveTileAllowedByFocus"/> flips, so the widget can re-sync.</summary>
        public event Action<bool>? ProviderForegroundChanged;

        /// <summary>
        /// Called by the taskbar layer's foreground hook the instant Windows switches windows. Re-runs
        /// detection off the UI thread (it can hit WMI) so leaving or returning to a provider app is
        /// reflected on the switch itself rather than up to one detect tick later.
        /// </summary>
        public void NotifyForegroundChanged()
        {
            if (!WidgetSettingsService.HideWhenProviderUnfocused)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    // DetectForegroundFast() never issues a UIA scan or WMI query, so switching to Zen
                    // (or any browser that is not currently serving a provider URL) returns in <1 ms.
                    // The grace timer's final re-check still uses full Detect() for accuracy.
                    UpdateProviderForeground(_detector.DetectForegroundFast() is not null);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Debug($"[focus] foreground re-detect failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Folds one detect pass into the focus state. Called from every path that resolves a provider, so
        /// the fast Synara/OpenCode/Cline switch handlers keep the widget up without waiting for a tick.
        /// </summary>
        private void UpdateProviderForeground(bool providerForeground)
        {
            bool changedTo;
            lock (_focusLock)
            {
                bool ownUiEngaged = !providerForeground
                    && (ActiveAppDetector.IsOwnProcessForeground() || IsOwnUiEngaged?.Invoke() == true);

                var (next, unfocusedSince) = ResolveProviderForegroundState(
                    providerForeground,
                    ownUiEngaged,
                    _providerForegroundActive,
                    _providerUnfocusedSinceUtc,
                    DateTime.UtcNow,
                    ProviderUnfocusHideDelay);

                _providerUnfocusedSinceUtc = unfocusedSince;
                if (_providerForegroundActive == next)
                {
                    // Inside the grace window: come back exactly when it expires rather than drifting to
                    // whenever the next detect tick happens to land. Skip re-arming when ownUiEngaged froze
                    // the state — the next genuine foreground change will re-evaluate normally.
                    if (!ownUiEngaged && next && unfocusedSince != DateTime.MinValue)
                        ArmUnfocusHideCheck(unfocusedSince);
                    return;
                }

                _providerForegroundActive = next;
                changedTo = next;
            }

            Diagnostics.Log.Debug($"[focus] provider foreground={changedTo} (hide-when-unfocused={WidgetSettingsService.HideWhenProviderUnfocused})");
            ProviderForegroundChanged?.Invoke(changedTo);
        }

        /// <summary>One-shot re-check at the end of the hide grace period. Caller holds <see cref="_focusLock"/>.</summary>
        private void ArmUnfocusHideCheck(DateTime unfocusedSinceUtc)
        {
            var remaining = ProviderUnfocusHideDelay - (DateTime.UtcNow - unfocusedSinceUtc) + TimeSpan.FromMilliseconds(15);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            if (_unfocusHideTimer is { } timer)
            {
                timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            _unfocusHideTimer = new Timer(
                _ =>
                {
                    try { UpdateProviderForeground(_detector.Detect() is not null); }
                    catch (Exception ex) { Diagnostics.Log.Debug($"[focus] grace re-check failed: {ex.Message}"); }
                },
                null,
                remaining,
                Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Pure core of <see cref="UpdateProviderForeground"/>. Shows instantly, hides only after the
        /// foreground has been provider-free for <paramref name="hideDelay"/>, and treats our own UI in
        /// front as neither — it holds both the state and the running grace period untouched.
        /// </summary>
        internal static (bool Active, DateTime UnfocusedSinceUtc) ResolveProviderForegroundState(
            bool providerForeground,
            bool ownUiEngaged,
            bool current,
            DateTime unfocusedSinceUtc,
            DateTime nowUtc,
            TimeSpan hideDelay)
        {
            if (ownUiEngaged && !providerForeground)
                return (current, unfocusedSinceUtc);

            if (providerForeground)
                return (true, DateTime.MinValue);

            if (unfocusedSinceUtc == DateTime.MinValue)
                unfocusedSinceUtc = nowUtc;

            return nowUtc - unfocusedSinceUtc < hideDelay
                ? (current, unfocusedSinceUtc)
                : (false, unfocusedSinceUtc);
        }
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
        /// <summary>Raised whenever foreground detection confidently associates a provider with a window.</summary>
        public event Action<ProviderId, IntPtr>? ProviderWindowObserved;

        public void Start()
        {
            if (_timer != null) return;
            // Warm WMI off-thread so the first terminal detection isn't blocked by COM cold start.
            _ = Task.Run(() => _detector.Prewarm());
            _detector.OpenCodeModelStateChanged += OnOpenCodeModelStateChanged;
            _detector.StartOpenCodeModelStateWatcher();
            _detector.ClineProviderStateChanged += OnClineProviderStateChanged;
            _detector.StartClineStateWatcher();
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

            // The model-button name changed (model switch / thread navigation), but Chromium may not
            // have flushed the new localStorage record to the LevelDB log yet, so the file watcher has
            // not fired and the snapshot cache still holds the pre-switch selection. Invalidate it now:
            // the next disk read rebuilds from the current log tail, so the authoritative state reader
            // reflects the new model as soon as the write lands instead of serving the stale snapshot
            // (which can linger for seconds until the flush reaches disk).
            SynaraStateReader.InvalidateDraftCache();

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
                _activeProviderSource = SynaraSource(host.Host);
                HoldSynaraProvider(provider);
                var previous = _lastActive;
                var providerChanged = previous != provider;
                if (providerChanged)
                    ClearPendingDetectedProvider();
                var hostChanged = !SameSynaraSelection(previousHost, host);
                _lastActive = provider;
                if (providerChanged)
                    PromoteRecentProvider(provider);

                if (_lastHasDetectedTool != true)
                {
                    _lastHasDetectedTool = true;
                    ActiveToolPresenceChanged?.Invoke(true);
                }

                UpdateProviderForeground(true);

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
            // A disabled provider must not leak fetches through Synara switch refreshes either
            // (issue #83): detection still tracks it, but nothing may fetch it.
            if (ProviderDiscoveryService.IsExplicitlyDisabled(targetProvider))
                return;

            try
            {
                var fresh = (await _service.FetchAsync(targetProvider, force: true).ConfigureAwait(false))
                    .WithSource(SourceFor(targetProvider));
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
            var foregroundSource = _detector.ActiveSource;
            var modelProvider = ActiveAppDetector.DetectOpenCodeProviderFromModelState();

            if (!ShouldReactToOpenCodeModelChange(foreground))
            {
                if (modelProvider is ProviderId backgroundProvider
                    && ShouldRefreshOpenCodeProvider(backgroundProvider))
                {
                    // Disabled providers must not leak fetches through OpenCode's background cache
                    // refresh either (issue #83). Skipping keeps the debounce observation recorded,
                    // so the same state-file event does not re-trigger.
                    if (!ProviderDiscoveryService.IsExplicitlyDisabled(backgroundProvider)
                        && !await RefreshProviderCacheSilentlyAsync(backgroundProvider).ConfigureAwait(false))
                        ForgetOpenCodeProviderObservation(backgroundProvider);
                }
                return;
            }

            var target = modelProvider ?? foreground!.Value;
            if (!IsOpenCodeProvider(target))
                return;

            // OpenCode rewrites its model/state files while the user types. The file event only matters
            // when it changes the quota surface (Zen vs Go); republishing the same provider forces a
            // network fetch and restarts the taskbar widget's refresh animation on every keystroke.
            if (!ShouldRefreshOpenCodeProvider(target))
                return;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ShouldReactToOpenCodeModelChange(_detector.Detect()))
                {
                    ForgetOpenCodeProviderObservation(target);
                    return;
                }

                var previous = _lastActive;
                if (previous != target)
                    ClearPendingDetectedProvider();
                _lastActive = target;
                _activeProviderSource = foregroundSource;
                PromoteRecentProvider(target);

                if (previous != target)
                    ActiveProviderChanged?.Invoke(target);

                PublishImmediateState(target);

                if (_lastHasDetectedTool != true)
                {
                    _lastHasDetectedTool = true;
                    ActiveToolPresenceChanged?.Invoke(true);
                }

                UpdateProviderForeground(true);
            }
            catch (Exception ex)
            {
                ForgetOpenCodeProviderObservation(target);
                Diagnostics.Log.Error(ex, "OpenCode model switch failed");
                return;
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                // Disabled providers must not leak fetches through OpenCode model-switch
                // refreshes either (issue #83). Detection bookkeeping above still runs; only
                // the network refresh and state publish are skipped.
                if (ProviderDiscoveryService.IsExplicitlyDisabled(target))
                    return;

                var fresh = (await _service.FetchAsync(target, force: true).ConfigureAwait(false))
                    .WithSource(SourceFor(target));
                if (!fresh.Ok)
                    ForgetOpenCodeProviderObservation(target);
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ShouldReactToOpenCodeModelChange(_detector.Detect()) || _lastActive != target)
                    {
                        ForgetOpenCodeProviderObservation(target);
                        return;
                    }

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
                ForgetOpenCodeProviderObservation(target);
                Diagnostics.Log.Error(ex, "OpenCode model switch refresh failed");
            }
        }

        internal static bool ShouldReactToOpenCodeModelChange(ProviderId? foregroundProvider)
            => foregroundProvider is ProviderId.OpenCode or ProviderId.OpenCodeGo;

        internal static bool IsOpenCodeProvider(ProviderId provider)
            => provider is ProviderId.OpenCode or ProviderId.OpenCodeGo;

        internal bool ShouldRefreshOpenCodeProvider(ProviderId provider)
        {
            lock (_openCodeModelStateLock)
            {
                if (!ShouldRefreshOpenCodeProvider(_lastObservedOpenCodeProvider, _hasObservedOpenCodeProvider, provider))
                    return false;

                _hasObservedOpenCodeProvider = true;
                _lastObservedOpenCodeProvider = provider;
                return true;
            }
        }

        private void ForgetOpenCodeProviderObservation(ProviderId provider)
        {
            lock (_openCodeModelStateLock)
            {
                if (_hasObservedOpenCodeProvider && _lastObservedOpenCodeProvider == provider)
                {
                    _hasObservedOpenCodeProvider = false;
                    _lastObservedOpenCodeProvider = null;
                }
            }
        }

        internal static bool ShouldRefreshOpenCodeProvider(
            ProviderId? lastObservedProvider,
            bool hasObservedProvider,
            ProviderId provider)
            => !hasObservedProvider || lastObservedProvider != provider;

        private void OnClineProviderStateChanged() => _ = HandleClineProviderSwitchAsync();

        internal static bool ShouldReactToClineChange(ProviderId? foregroundProvider)
            => foregroundProvider is ProviderId.Cline or ProviderId.ClinePass;

        internal static bool IsClineProvider(ProviderId provider)
            => provider is ProviderId.Cline or ProviderId.ClinePass;

        /// <summary>
        /// providers.json changed: if a Cline terminal is focused, switch the highlighted card to the
        /// newly-active surface (usage-billing vs ClinePass) in realtime; otherwise just refresh the
        /// affected card's cache in the background.
        /// </summary>
        private async Task HandleClineProviderSwitchAsync()
        {
            var foreground = _detector.Detect();
            var stateProvider = ActiveAppDetector.DetectClineProviderFromState();

            if (!ShouldReactToClineChange(foreground))
            {
                if (stateProvider is ProviderId backgroundProvider)
                    await RefreshProviderCacheSilentlyAsync(backgroundProvider).ConfigureAwait(false);
                return;
            }

            var target = stateProvider ?? foreground!.Value;
            if (!IsClineProvider(target))
                return;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ShouldReactToClineChange(_detector.Detect()))
                    return;

                var previous = _lastActive;
                if (previous != target)
                    ClearPendingDetectedProvider();
                _lastActive = target;
                _activeProviderSource = _detector.ActiveSource;
                PromoteRecentProvider(target);

                if (previous != target)
                    ActiveProviderChanged?.Invoke(target);

                PublishImmediateState(target);
                UpdateProviderForeground(true);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Error(ex, "Cline provider switch failed");
                return;
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                // Disabled providers must not leak fetches through Cline switch refreshes
                // either (issue #83).
                if (ProviderDiscoveryService.IsExplicitlyDisabled(target))
                    return;

                var fresh = (await _service.FetchAsync(target, force: true).ConfigureAwait(false))
                    .WithSource(SourceFor(target));
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ShouldReactToClineChange(_detector.Detect()) || _lastActive != target)
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
                Diagnostics.Log.Error(ex, "Cline provider switch refresh failed");
            }
        }

        private async Task<bool> RefreshProviderCacheSilentlyAsync(ProviderId provider)
        {
            try
            {
                var result = await _service.FetchAsync(provider, force: true).ConfigureAwait(false);
                return result.Ok;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, $"Background refresh for {provider} failed");
                return false;
            }
        }

        private void PublishImmediateState(ProviderId target)
        {
            UsageResult snapshot;
            if (_service.TryGetCached(target, out var cached))
                snapshot = cached.WithSource(SourceFor(target));
            else if (_service.TryGetLastSuccessfulLiveResult(target, out var lastSuccess))
                snapshot = lastSuccess.WithSource(SourceFor(target));
            else if (_service.Get(target) is { } provider)
                snapshot = UsageResult.Pending(target, provider, "Loading...").WithSource(SourceFor(target));
            else
                return;

            LastState = snapshot;
            StateChanged?.Invoke(snapshot);
        }

        /// <summary>
        /// Refreshes one pinned tile's provider and publishes it through <see cref="StateChanged"/> so the
        /// widget routes it to that tile. The tick only ever fetches the ACTIVE provider, so without this a
        /// pinned tile would freeze on its boot snapshot. Cheap: <see cref="UsageService.FetchAsync"/> is
        /// cache-TTL gated, so most calls return the cached snapshot without touching the network. Leaves
        /// <see cref="LastState"/> and the active provider alone. Disabled providers never hold tiles, so
        /// refreshing one would be a fetch leak and is refused (issue #83).
        /// </summary>
        public async Task RefreshWidgetProviderAsync(ProviderId id)
        {
            if (ProviderDiscoveryService.IsExplicitlyDisabled(id))
                return;

            lock (_widgetRefreshInFlight)
            {
                if (!_widgetRefreshInFlight.Add(id))
                    return;
            }

            try
            {
                var result = (await _service.FetchAsync(id).ConfigureAwait(false)).WithSource(SourceFor(id));
                StateChanged?.Invoke(result);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, $"Widget provider refresh for {id} failed");
            }
            finally
            {
                lock (_widgetRefreshInFlight)
                    _widgetRefreshInFlight.Remove(id);
            }
        }

        /// <summary>Fetch all providers (cached) for the multi-provider view.</summary>
        public async Task<IReadOnlyList<UsageResult>> FetchAllAsync(bool force = false)
        {
            var tasks = _service.All
                .Select(p => Task.Run(() => _service.FetchAsync(p.Id, force)))
                .ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var sourcedResults = results.Select(result => result.WithSource(SourceFor(result.Id))).ToArray();
            return SortByRecentActivity(sourcedResults, RecentProviders, ActiveProvider);
        }

        /// <summary>Fetch providers needed for the dashboard and report each result as it arrives.</summary>
        public async Task FetchAllProgressiveAsync(
            bool force,
            Action<UsageResult> onResult,
            CancellationToken ct = default)
        {
            var active = ActiveProvider;
            var tasks = _service.All
                .Where(p => ShouldFetchForDashboard(p.Id, force, active))
                .Select(p => Task.Run(() => _service.FetchAsync(p.Id, force, ct), ct))
                .ToList();

            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                onResult(result.WithSource(SourceFor(result.Id)));
            }
        }

        /// <summary>
        /// Provider filter for dashboard fetch passes. A manual refresh widens the cache policy
        /// (force), never the provider set: explicitly-disabled providers are never fetched —
        /// not when forced, and not when they are the active provider (issue #83).
        /// </summary>
        internal static bool ShouldFetchForDashboard(ProviderId id, bool force, ProviderId? active)
            => !ProviderDiscoveryService.IsExplicitlyDisabled(id)
                && (force || ProviderDiscoveryService.ShouldFetch(id, active));

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
            ProviderSource detectedSource;
            SynaraStateReader.SynaraSelection? detectedSynaraHost;
            try
            {
                detected = _detector.Detect();
                detectedSource = _detector.ActiveSource;
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
                    detectedSource = SynaraSource(synaraHost.Host);
                    HoldSynaraProvider(synaraHost.Provider);
                }
                else if (ShouldHoldSynaraProvider(detected))
                {
                    detected = _synaraHoldProvider;
                    detectedSource = _activeProviderSource;
                }
                else
                {
                    ActiveSynaraHost = null;
                    ClearSynaraHold();
                }

                // Foreground bookkeeping runs on every tick, including the ones that end in the no-tool
                // early-out below, so the hide grace period keeps counting while nothing is detected.
                UpdateProviderForeground(detected is not null);
                if (detected is null)
                    ClearPendingDetectedProvider();

                var hasDetectedTool = detected != null || ShouldAssumeToolStillRunning() || ProbeToolPresence();
                if (!hasDetectedTool)
                {
                    if (_lastHasDetectedTool != false)
                    {
                        _lastHasDetectedTool = false;
                        _lastActive = null;
                        _lastLogged = null;
                        _activeProviderSource = ProviderSource.Unknown;
                        ClearPendingDetectedProvider();
                        ActiveSynaraHost = null;
                        ClearSynaraHold(force: true);
                        ActiveToolPresenceChanged?.Invoke(false);
                    }
                    return;
                }

                ProviderId? previousActive = _lastActive;
                if (detected is ProviderId matchingActive && previousActive == matchingActive)
                    ClearPendingDetectedProvider();
                if (detected is ProviderId p
                    && (previousActive == p
                        || AcceptDetectedProvider(p)))
                {
                    _lastActive = p;
                    _activeProviderSource = detectedSource;
                    PromoteRecentProvider(p);
                    var foregroundWindow = User32.GetForegroundWindow();
                    if (foregroundWindow != IntPtr.Zero)
                        ProviderWindowObserved?.Invoke(p, foregroundWindow);
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
                // A disabled provider must not leak network fetches either: nothing is allowed
                // to display it, so only the widget fallback below stays fresh.
                if (!ProviderDiscoveryService.IsExplicitlyDisabled(target))
                {
                    var result = await _service.FetchAsync(target, force).ConfigureAwait(false);
                    result = result.WithSource(SourceFor(target));

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

                await PublishWidgetProviderStateAsync(target, force).ConfigureAwait(false);
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

        /// <summary>
        /// The tick fetches the ACTIVE provider, but the taskbar widget only accepts results for
        /// <see cref="WidgetDisplayProvider"/> (e.g. the active provider is hidden from the widget, or none
        /// is detected yet). When the two differ the widget never received a result and kept rendering the
        /// startup placeholder while the flyout showed live data — issue #21. Publish the widget's provider
        /// too, without touching <see cref="LastState"/> (that tracks the active provider).
        /// </summary>
        private async Task PublishWidgetProviderStateAsync(ProviderId published, bool force)
        {
            if (WidgetDisplayProvider is not { } widgetTarget || widgetTarget == published)
                return;

            var widgetResult = await _service.FetchAsync(widgetTarget, force).ConfigureAwait(false);
            StateChanged?.Invoke(widgetResult.WithSource(SourceFor(widgetTarget)));
        }

        private void PromoteRecentProvider(ProviderId provider)
        {
            lock (_recentLock)
            {
                _recentProviders.Remove(provider);
                _recentProviders.Insert(0, provider);
            }
        }

        private ProviderSource SourceFor(ProviderId provider)
            => _lastActive == provider ? _activeProviderSource : ProviderSource.Unknown;

        private bool AcceptDetectedProvider(ProviderId provider)
            => ShouldAcceptDetectedProvider(
                ref _pendingDetectedProvider,
                ref _pendingDetectedProviderSamples,
                provider,
                RequiredProviderSwitchSamples);

        internal static bool ShouldAcceptDetectedProvider(
            ref ProviderId? pendingProvider,
            ref int pendingSamples,
            ProviderId provider,
            int requiredSamples)
        {
            if (pendingProvider != provider)
            {
                pendingProvider = provider;
                pendingSamples = 1;
                if (requiredSamples > 1)
                    return false;
                pendingProvider = null;
                pendingSamples = 0;
                return true;
            }

            pendingSamples++;
            if (pendingSamples < requiredSamples)
                return false;

            pendingProvider = null;
            pendingSamples = 0;
            return true;
        }

        private void ClearPendingDetectedProvider()
        {
            _pendingDetectedProvider = null;
            _pendingDetectedProviderSamples = 0;
        }

        private static ProviderSource SynaraSource(HostApp host)
            => new(
                ProviderSourceKind.HostApp,
                host == HostApp.T3Code ? "T3 Code" : "Synara",
                host == HostApp.T3Code ? "t3code" : "synara");

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
