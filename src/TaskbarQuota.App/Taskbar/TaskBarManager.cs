using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Interop;
using TaskbarQuota.Usage;
using TaskbarQuota.AgentActivity;

namespace TaskbarQuota.Taskbar
{
    /// <summary>
    /// Owns the tray icon and the active usage surface (taskbar widgets or floating window), and pushes
    /// coordinator state into that surface.
    /// </summary>
    internal static class TaskBarManager
    {
        private static TrayIconWithContextMenu? _trayIcon;
        private static System.Drawing.Icon? _trayIconSource;
        private static readonly Dictionary<IntPtr, TaskBarWidget> Widgets = new();
        // Reused snapshot of Widgets.Values, so iterating it while a callback may mutate the dictionary
        // doesn't allocate. Only valid until the next SnapshotWidgets call, and only used on the UI thread.
        private static readonly List<TaskBarWidget> _widgetBuffer = new();
        private static FloatingUsageWindow? _floatingWindow;
        private static FlyoutWindow? _flyout;
        private static DispatcherQueue? _dispatcher;
        private static Action? _showMainWindow;
        private static DispatcherTimer? _widgetHealthTimer;
        private static DispatcherTimer? _topologyRecoveryTimer;
        private static DispatcherTimer? _activityTimer;
        private static CancellationTokenSource? _activityCts;
        private static readonly TimeSpan ActiveActivityInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan IdleActivityInterval = TimeSpan.FromSeconds(10);
        private static bool _initialized;
        private static bool _isReconcilingWidgets;
        private static bool _topologyRecoveryPending;
        private static bool _topologyForceReset;
        private static int _topologyRecoveryAttempts;
        private static string _topologyRecoveryReason = string.Empty;
        private const int MaxTopologyRecoveryAttempts = 12;
        private static readonly TopologyStabilityTracker TopologyStability = new();
        private static readonly Dictionary<IntPtr, int> MissingTaskbarObservations = new();
        private static readonly AdaptiveDisplayProviderState AdaptiveDisplayProviders = new();
        private static ProviderId? _lastLoggedWidgetApplyProvider;
        private static WidgetSurfaceMode _activeSurface = WidgetSurfaceMode.Taskbar;
        // Foreground hook: fires the instant Windows switches windows so the focus-follows-provider
        // widget reacts on the switch itself instead of waiting for the next 500 ms detect tick.
        private static ActiveApp.ForegroundWatcher? _foregroundWatcher;
        private static SessionTopologyWatcher? _sessionTopologyWatcher;

        private static bool IsFloatingSurface => _activeSurface == WidgetSurfaceMode.Floating;

        public static void Initialize(DispatcherQueue dispatcher, Action showMainWindow)
        {
            _dispatcher = dispatcher;
            _showMainWindow = showMainWindow;

            CreateTrayIcon();
            ApplySurfaceFromSettings();

            if (!_initialized)
            {
                UsageCoordinator.Instance.StateChanged += OnStateChanged;
                UsageCoordinator.Instance.ActiveProviderChanged += OnActiveProviderChanged;
                UsageCoordinator.Instance.ActiveToolPresenceChanged += OnActiveToolPresenceChanged;
                UsageCoordinator.Instance.ProviderWindowObserved += OnProviderWindowObserved;
                AgentActivityService.Instance.Changed += OnActivityChanged;
                UsageCoordinator.Instance.ProviderForegroundChanged += OnProviderForegroundChanged;
                // Lets the coordinator's focus tracker treat our flyout and an active widget drag as neutral
                // instead of as the user having left the provider app. Otherwise the opt-in hide-on-unfocus
                // path can hide the widget during the drag and restore its old position.
                UsageCoordinator.Instance.IsOwnUiEngaged = () =>
                    _flyout?.IsShown == true
                    || TaskBarWidget.IsAnyUserRepositioning
                    || _floatingWindow?.IsDragging == true;
                WidgetSettingsService.Changed += OnWidgetSettingsChanged;
                App.Quitting += OnQuitting;
                // Installed from the UI thread on purpose: WINEVENT_OUTOFCONTEXT callbacks arrive through
                // that thread's message pump, which this one has and background threads do not.
                _foregroundWatcher = new ActiveApp.ForegroundWatcher();
                _foregroundWatcher.ForegroundChanged += OnForegroundChanged;
                _foregroundWatcher.WindowMoveSizeEnded += OnWindowMoveSizeEnded;
                _foregroundWatcher.Start();
                try
                {
                    _sessionTopologyWatcher = new SessionTopologyWatcher();
                    _sessionTopologyWatcher.Changed += OnTopologyChanged;
                    _sessionTopologyWatcher.Start();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Session/display recovery watcher could not be started; periodic health recovery remains active");
                    if (_sessionTopologyWatcher is { } topologyWatcher)
                    {
                        topologyWatcher.Changed -= OnTopologyChanged;
                        topologyWatcher.Dispose();
                        _sessionTopologyWatcher = null;
                    }
                }
                _initialized = true;
            }

            StartWidgetHealthTimer();
            ConfigureActivityTimer();
            OnActiveToolPresenceChanged(UsageCoordinator.Instance.IsActiveToolPresent);
        }

        private static void ApplySurfaceFromSettings()
        {
            var desired = WidgetSettingsService.CurrentSurface;
            if (desired == WidgetSurfaceMode.Floating)
            {
                DisposeAllTaskbarWidgets();
                EnsureFloatingWindow();
                _activeSurface = WidgetSurfaceMode.Floating;
                SyncFloatingState();
            }
            else
            {
                DisposeFloatingWindow();
                _activeSurface = WidgetSurfaceMode.Taskbar;
                if (_topologyRecoveryPending)
                {
                    if (TryRecoverTaskbarTopology())
                        CompleteTopologyRecovery();
                }
                else
                {
                    EnsureWidgets();
                }
                SyncWidgetState();
                ScheduleFloatingPrewarm();
            }
        }

        private static void EnsureFloatingWindow()
        {
            if (_floatingWindow is { IsAlive: true })
                return;

            try { _floatingWindow?.Close(); } catch { }
            _floatingWindow = null;

            try
            {
                var window = new FloatingUsageWindow();
                window.HydrateProvider = provider => HydrateResult(UsageCoordinator.Instance, provider);
                window.Clicked += () => _dispatcher?.TryEnqueue(() => ToggleFlyout(window.Handle));
                window.ActivityClicked += item => _dispatcher?.TryEnqueue(
                    () => ToggleActivityFlyout(window.Handle, item?.Id));
                _floatingWindow = window;
                window.Prewarm();
                PrewarmFlyout();
                Log.Information("Floating usage window created");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create floating usage window");
                try { _floatingWindow?.Close(); } catch { }
                _floatingWindow = null;
            }
        }

        private static void DisposeFloatingWindow()
        {
            if (_floatingWindow is null)
                return;

            try { _floatingWindow.Close(); } catch (Exception ex) { Log.Warning(ex, "Failed to close floating usage window"); }
            _floatingWindow = null;
        }

        private static void DisposeAllTaskbarWidgets()
        {
            foreach (var widget in Widgets.Values.ToArray())
            {
                try { widget.Dispose(); } catch (Exception ex) { Log.Warning(ex, "Failed to dispose taskbar widget"); }
            }
            Widgets.Clear();
            TaskbarSpace.ResetAvailableWidth();
        }

        /// <summary>
        /// Pushes providers, activity, and visibility into the floating window.
        /// Returns false when the surface is unavailable or has nothing to show.
        /// </summary>
        private static bool SyncFloatingSurface()
        {
            if (_floatingWindow is not { IsAlive: true })
                return false;

            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;
            var activity = AgentActivityService.Instance.Snapshot;

            _floatingWindow.SetActivitySnapshot(activity);
            _floatingWindow.SetDisplayProviders(providers, coordinator.ActiveProvider);

            // Consult the content actually applied by the floating host. A transient empty scan may be
            // held for a short grace period; using the raw snapshot here hid the entire window anyway and
            // defeated that protection.
            if (!_floatingWindow.HasVisibleContent)
            {
                _floatingWindow.SetVisible(false);
                return false;
            }

            _floatingWindow.SetVisible(true);
            return true;
        }

        private static void SyncFloatingState()
        {
            if (!SyncFloatingSurface())
                return;

            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;

            bool needsFetch = false;
            foreach (var provider in providers)
            {
                var toApply = HydrateResult(coordinator, provider);
                if (toApply is { } result)
                {
                    _floatingWindow!.ApplyResult(result, force: true);
                    LogWidgetApply(result.Id, "floating-sync");
                }

                if (toApply is null or { Ok: false })
                    needsFetch = true;
            }

            if (needsFetch)
                _ = coordinator.TickAsync(force: true);
        }

        /// <summary>
        /// True when the floating surface should be showing content but the window is hidden or gone.
        /// Used by the health timer so a transient hide does not become permanent.
        /// </summary>
        private static bool NeedsFloatingResync()
        {
            if (_floatingWindow is not { IsAlive: true })
                return true;

            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;
            bool shouldShow = providers.Count > 0 || _floatingWindow.HasVisibleContent;

            return shouldShow && !_floatingWindow.IsShown;
        }

        private static void StartActivityTimer()
        {
            if (_activityTimer != null || !WidgetSettingsService.EnableAgentActivityMonitoring)
                return;

            _activityCts = new CancellationTokenSource();
            var cancellationToken = _activityCts.Token;
            // Start fast so a transcript created just after launch is not held behind the ten-second idle
            // interval. The first completed timer refresh selects the normal active/idle cadence.
            _activityTimer = new DispatcherTimer { Interval = ActiveActivityInterval };
            _activityTimer.Tick += async (_, _) =>
            {
                await AgentActivityService.Instance.RefreshFromTranscriptsAsync(cancellationToken);
                if (_activityTimer is { } timer)
                    timer.Interval = AgentActivityService.Instance.Snapshot.HasLiveItems
                        ? ActiveActivityInterval
                        : IdleActivityInterval;
            };
            _activityTimer.Start();
            _ = AgentActivityService.Instance.RefreshFromTranscriptsAsync(cancellationToken);
        }

        private static void ConfigureActivityTimer()
        {
            if (WidgetSettingsService.EnableAgentActivityMonitoring)
            {
                StartActivityTimer();
                return;
            }

            _activityTimer?.Stop();
            _activityTimer = null;
            _activityCts?.Cancel();
            _activityCts?.Dispose();
            _activityCts = null;
            AgentActivityService.Instance.Clear();
        }

        private static void CreateTrayIcon()
        {
            var open = new PopupMenuItem("Open TaskbarQuota", (_, _) => _dispatcher?.TryEnqueue(() => _showMainWindow?.Invoke()));
            var activity = new PopupMenuItem("Open agent activity", (_, _) => _dispatcher?.TryEnqueue(
                () => ToggleActivityFlyout(anchorHandle: null, selectedActivityId: null)));
            var move = new PopupMenuItem("Move usage widget", (_, _) => _dispatcher?.TryEnqueue(StartMoveActiveSurface));
            var reset = new PopupMenuItem("Reset widget positions", (_, _) => _dispatcher?.TryEnqueue(ResetActiveSurfacePositions));
            var quit = new PopupMenuItem("Quit", (_, _) => _dispatcher?.TryEnqueue(App.Quit));

            System.Drawing.Icon? icon = null;
            try
            {
                var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "TaskBarQuota.ico");
                if (System.IO.File.Exists(icoPath))
                    icon = new System.Drawing.Icon(icoPath, 48, 48);
                else
                    icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            }
            catch { }

            _trayIcon = new TrayIconWithContextMenu
            {
                ContextMenu = new PopupMenu { Items = { open, activity, new PopupMenuSeparator(), move, reset, new PopupMenuSeparator(), quit } },
                ToolTip = "TaskbarQuota",
            };
            _trayIcon.Create();
            if (icon != null)
            {
                _trayIconSource = icon;
                _trayIcon.Icon = icon.Handle;
            }
            _trayIcon.MessageWindow.MouseEventReceived += (_, e) =>
            {
                if (e.MouseEvent is MouseEvent.IconLeftMouseUp or MouseEvent.IconLeftDoubleClick)
                    _dispatcher?.TryEnqueue(() => _showMainWindow?.Invoke());
            };
        }

        private static void StartMoveActiveSurface()
        {
            if (IsFloatingSurface)
            {
                _floatingWindow?.StartDragging();
                return;
            }

            PrimaryWidget()?.StartDragging();
        }

        private static void ResetActiveSurfacePositions()
        {
            if (IsFloatingSurface)
            {
                _floatingWindow?.ResetPosition();
                return;
            }

            foreach (var widget in Widgets.Values.ToArray())
                widget.UpdatePosition(resetManualPosition: true);
        }

        private static void StartWidgetHealthTimer()
        {
            if (_widgetHealthTimer != null)
                return;

            _widgetHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _widgetHealthTimer.Tick += (_, _) =>
            {
                if (IsFloatingSurface)
                {
                    bool recreated = false;
                    if (_floatingWindow is null || !_floatingWindow.IsAlive)
                    {
                        EnsureFloatingWindow();
                        recreated = true;
                    }

                    // Recreate is empty until hydrated. Also re-sync periodically so a hide from a
                    // transient empty provider set (or opacity stuck at 0 after settings thrash) cannot
                    // leave the HUD gone until the next process restart.
                    if (recreated || NeedsFloatingResync())
                        SyncFloatingState();

                    RefreshPinnedTiles();
                    Services.PinBudgetService.EnforceBudget();
                    return;
                }

                EnsureWidgets();
                RefreshPinnedTiles();
                // The free span is only known once a widget has measured it, so a set pinned before that
                // (or pinned when the bar was emptier) is reconciled here rather than rendering badly.
                // EnforceBudget early-outs when neither the span nor the pinned set has moved, which is
                // every tick but the few that follow a real change.
                Services.PinBudgetService.EnforceBudget();
                // Re-run the tile-fit math against the gap the last position pass measured, so tiles that
                // were trimmed off a crowded taskbar come back once there is room for them again.
                // Iterated over the reused buffer rather than Widgets.Values.ToArray(), which allocated an
                // array every five seconds for the life of the process.
                SnapshotWidgets();
                foreach (var widget in _widgetBuffer)
                {
                    if (widget.IsAlive)
                        widget.RefreshLayout();
                }
            };
            _widgetHealthTimer.Start();
        }

        private static void EnsureWidgets()
        {
            if (IsFloatingSurface)
                return;

            if (_isReconcilingWidgets)
                return;

            _isReconcilingWidgets = true;
            try
            {
                if (!TaskbarWindowTarget.TryFindAll(out var targets))
                {
                    Log.Warning("Could not enumerate Windows taskbars; keeping existing widgets until the next health check");
                    return;
                }
                var targetsByHandle = targets.ToDictionary(target => target.Handle);

                foreach (var pair in Widgets.ToArray())
                {
                    string? recreateReason = null;
                    if (!pair.Value.IsAlive)
                        recreateReason = "host window unavailable";
                    else if (!targetsByHandle.TryGetValue(pair.Key, out var target))
                    {
                        int misses = MissingTaskbarObservations.TryGetValue(pair.Key, out int previous)
                            ? previous + 1
                            : 1;
                        MissingTaskbarObservations[pair.Key] = misses;
                        if (ShouldRemoveMissingTaskbar(misses, hostAlive: true))
                            recreateReason = $"taskbar absent for {misses} consecutive scans";
                    }
                    else if (!pair.Value.IsHostContentReady)
                        recreateReason = "XAML host content unavailable";
                    else if (pair.Value.IsConfirmedTargetMismatch(target))
                        recreateReason = $"display identity changed ({pair.Value.DisplayKey}->{target.DisplayKey})";

                    if (targetsByHandle.ContainsKey(pair.Key))
                        MissingTaskbarObservations.Remove(pair.Key);

                    if (recreateReason is null)
                    {
                        // A stable host can absorb per-monitor DPI changes in place. Recreating the complete
                        // XAML island on one anomalous reading caused mixed-DPI systems to enter a slow
                        // destroy/create loop whenever Explorer was rebuilding its display topology.
                        pair.Value.RefreshDpiFromWindows();
                        continue;
                    }

                    Widgets.Remove(pair.Key);
                    MissingTaskbarObservations.Remove(pair.Key);
                    Log.Warning($"Taskbar widget recreating: taskbar=0x{pair.Key.ToInt64():X}, reason={recreateReason}");
                    try { pair.Value.Dispose(); }
                    catch (Exception ex) { Log.Warning(ex, "Failed to dispose missing taskbar widget"); }
                }

                foreach (var target in targets)
                {
                    if (!Widgets.ContainsKey(target.Handle))
                        CreateWidget(target);
                }

                foreach (var handle in MissingTaskbarObservations.Keys.Where(handle => !Widgets.ContainsKey(handle)).ToArray())
                    MissingTaskbarObservations.Remove(handle);
            }
            finally
            {
                _isReconcilingWidgets = false;
            }
        }

        private static void CreateWidget(TaskbarWindowTarget target)
        {
            TaskBarWidget? widget = null;
            try
            {
                widget = new TaskBarWidget(target);
                widget.Initialize();
                widget.Destroying += (sender, _) =>
                {
                    if (sender is TaskBarWidget destroyedWidget)
                        _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () => OnWidgetDestroying(destroyedWidget));
                };
                widget.HydrateProvider = provider => HydrateResult(UsageCoordinator.Instance, provider);
                widget.Clicked += () => _dispatcher?.TryEnqueue(() => ToggleFlyout(widget));
                widget.ActivityClicked += item => _dispatcher?.TryEnqueue(
                    () => ToggleActivityFlyout(widget, item?.Id));
                Widgets[target.Handle] = widget;
                SyncWidgetState(widget);
                PrewarmFlyout();
                Log.Information($"Taskbar widget created: taskbar=0x{target.Handle.ToInt64():X}, primary={target.IsPrimary}");
            }
            catch (Exception ex)
            {
                try { widget?.Dispose(); } catch { }
                Log.Error(ex, $"Failed to create taskbar widget for taskbar=0x{target.Handle.ToInt64():X}");
            }
        }

        private static void OnWidgetDestroying(TaskBarWidget widget)
        {
            if (Widgets.TryGetValue(widget.TaskbarHandle, out var current) && ReferenceEquals(current, widget))
                Widgets.Remove(widget.TaskbarHandle);

            try { widget.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "Failed to dispose destroyed taskbar widget"); }
        }

        private static TaskBarWidget? PrimaryWidget()
            => Widgets.Values.FirstOrDefault(widget => widget.IsAlive && widget.IsPrimaryTaskbar)
                ?? Widgets.Values.FirstOrDefault(widget => widget.IsAlive);

        private static IReadOnlyList<ProviderId> ProvidersForWidget(
            TaskBarWidget widget,
            IReadOnlyList<ProviderId> providers)
        {
            var available = Widgets.Values
                .Where(candidate => candidate.IsAlive)
                .Select(candidate => candidate.DisplayKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string primary = PrimaryWidget()?.DisplayKey ?? widget.DisplayKey;
            var mode = WidgetSettingsService.CurrentTaskbarPlacement;
            if (mode == TaskbarPlacementMode.Adaptive
                && AdaptiveProviderForDisplay(widget.DisplayKey) is { } displayActive
                && WidgetSettingsService.IsProviderVisible(displayActive))
            {
                providers = new[] { displayActive }
                    .Concat(providers)
                    .Distinct()
                    .ToArray();
            }

            return TaskbarContentRouter.ProvidersForDisplay(
                providers,
                mode,
                WidgetSettingsService.SelectedTaskbarDisplayKey,
                widget.DisplayKey,
                primary,
                available,
                WidgetSettingsService.GetAdaptiveProviderDisplay,
                WidgetSettingsService.IsProviderPinned,
                WidgetSettingsService.GetPinnedProviderDisplay);
        }

        private static ProviderId? ActiveProviderForWidget(TaskBarWidget widget, ProviderId? globalActive)
            => WidgetSettingsService.CurrentTaskbarPlacement == TaskbarPlacementMode.Adaptive
                ? AdaptiveProviderForDisplay(widget.DisplayKey)
                : globalActive;

        private static ProviderId? AdaptiveProviderForDisplay(string displayKey)
            => AdaptiveDisplayProviders.GetProvider(
                displayKey,
                hwnd => User32.IsWindow(hwnd)
                    && string.Equals(
                        TaskbarWindowTarget.GetDisplayKeyForWindow(hwnd),
                        displayKey,
                        StringComparison.OrdinalIgnoreCase));

        internal static bool ShouldRemoveMissingTaskbar(int consecutiveMisses, bool hostAlive)
            => !hostAlive || consecutiveMisses >= 2;

        private static void OnTopologyChanged(TopologyChange change)
            => _dispatcher?.TryEnqueue(() => ScheduleTopologyRecovery(change));

        private static void ScheduleTopologyRecovery(TopologyChange change)
        {
            _topologyRecoveryPending = true;
            _topologyForceReset |= change.RequiresHostReset;
            _topologyRecoveryAttempts = 0;
            _topologyRecoveryReason = change.Reason;
            TopologyStability.Reset();

            _topologyRecoveryTimer ??= CreateTopologyRecoveryTimer();
            ArmTopologyRecoveryTimer();
            Log.Information($"Taskbar topology recovery scheduled: reason={change.Reason}, resetHosts={change.RequiresHostReset}");
        }

        private static DispatcherTimer CreateTopologyRecoveryTimer()
        {
            var timer = new DispatcherTimer();
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_topologyRecoveryPending)
                    return;

                if (TryRecoverTaskbarTopology())
                {
                    CompleteTopologyRecovery();
                    return;
                }

                _topologyRecoveryAttempts++;
                if (_topologyRecoveryAttempts < MaxTopologyRecoveryAttempts)
                    ArmTopologyRecoveryTimer();
                else
                    Log.Warning($"Taskbar topology did not stabilize after {_topologyRecoveryAttempts} attempts; health checks will continue recovery ({_topologyRecoveryReason})");
            };
            return timer;
        }

        private static void ArmTopologyRecoveryTimer()
        {
            if (_topologyRecoveryTimer is not { } timer)
                return;

            timer.Stop();
            timer.Interval = SessionTopologyWatcher.RetryDelay(_topologyRecoveryAttempts);
            timer.Start();
        }

        private static bool TryRecoverTaskbarTopology()
        {
            if (IsFloatingSurface)
                return true;

            if (!TaskbarWindowTarget.TryFindAll(out var targets) || targets.Count == 0)
            {
                TopologyStability.Reset();
                return false;
            }

            string signature = string.Join(
                "|",
                targets.Select(target => $"{target.Handle.ToInt64():X}:{target.DisplayKey}:{target.IsPrimary}"));
            if (!TopologyStability.Observe(signature))
                return false;

            if (_topologyForceReset)
            {
                Log.Information($"Rebuilding taskbar hosts after {_topologyRecoveryReason}");
                DisposeAllTaskbarWidgets();
                MissingTaskbarObservations.Clear();
                _topologyForceReset = false;
            }

            EnsureWidgets();
            SyncWidgetState();

            return targets.All(target =>
                Widgets.TryGetValue(target.Handle, out var widget)
                && widget.IsAlive
                && widget.IsHostContentReady
                && string.Equals(widget.DisplayKey, target.DisplayKey, StringComparison.OrdinalIgnoreCase));
        }

        private static void CompleteTopologyRecovery()
        {
            _topologyRecoveryTimer?.Stop();
            _topologyRecoveryPending = false;
            _topologyForceReset = false;
            _topologyRecoveryAttempts = 0;
            TopologyStability.Reset();
            Log.Information($"Taskbar topology recovery completed ({_topologyRecoveryReason})");
            _topologyRecoveryReason = string.Empty;
        }

        private static AgentActivitySnapshot ActivityForWidget(
            TaskBarWidget widget,
            AgentActivitySnapshot snapshot)
        {
            var available = Widgets.Values
                .Where(candidate => candidate.IsAlive)
                .Select(candidate => candidate.DisplayKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string primary = PrimaryWidget()?.DisplayKey ?? widget.DisplayKey;
            return TaskbarContentRouter.ActivityForDisplay(
                snapshot,
                WidgetSettingsService.CurrentTaskbarPlacement,
                WidgetSettingsService.SelectedTaskbarDisplayKey,
                widget.DisplayKey,
                primary,
                available,
                WidgetSettingsService.GetAdaptiveProviderDisplay);
        }

        private static void SyncWidgetState()
        {
            if (IsFloatingSurface)
            {
                SyncFloatingState();
                return;
            }

            foreach (var widget in Widgets.Values.ToArray())
                SyncWidgetState(widget);
        }

        private static void SyncWidgetState(TaskBarWidget widget)
        {
            if (!widget.IsAlive)
                return;

            var coordinator = UsageCoordinator.Instance;
            var providers = ProvidersForWidget(widget, coordinator.WidgetDisplayProviders);
            var sourceActivity = AgentActivityService.Instance.Snapshot;
            var activity = ActivityForWidget(widget, sourceActivity);

            // A non-empty source filtered to empty by the selected-screen policy is intentional and must
            // hide immediately. Reserve the empty grace period for genuinely empty scanner snapshots.
            widget.SetActivitySnapshot(activity, allowEmptyGrace: sourceActivity.Primary is null);
            // Keep outgoing tiles bound until the host fade completes. Clearing them first makes the
            // animation run over an empty window and turns a cross-screen handoff into a visible blink.
            if (providers.Count == 0)
            {
                widget.HideDisplayProviders(
                    ActiveProviderForWidget(widget, coordinator.ActiveProvider),
                    keepActivityVisible: widget.HasVisibleActivity);
                return;
            }

            widget.SetDisplayProviders(providers, ActiveProviderForWidget(widget, coordinator.ActiveProvider));
            widget.SetVisible(true);

            bool needsFetch = false;
            foreach (var provider in providers)
            {
                var toApply = HydrateResult(coordinator, provider);
                if (toApply is { } result)
                {
                    widget.ApplyResult(result, force: true);
                    LogWidgetApply(result.Id, "sync");
                }

                // Hydrating from a placeholder/failed snapshot leaves the tile showing a non-value while
                // the flyout fetches its own data. Kick a fetch so the widget resolves on its own (#21).
                if (toApply is null or { Ok: false })
                    needsFetch = true;
            }

            if (needsFetch)
                _ = coordinator.TickAsync(force: true);
        }

        /// <summary>
        /// Best snapshot available to seed a tile: the last active publish, then either cache tier, then a
        /// Pending placeholder. Null only when the provider is unknown to the usage service.
        /// </summary>
        private static UsageResult? HydrateResult(UsageCoordinator coordinator, ProviderId provider)
        {
            if (coordinator.Service.TryGetCached(provider, out var cached))
                return cached;
            // A failed refresh is cached deliberately. Prefer that current failure over LastState,
            // which may still contain the previous successful snapshot and would resurrect stale quota
            // values when the widget is recreated (especially after cookie/auth failures).
            if (coordinator.LastState is { } last && last.Id == provider)
                return last;
            if (coordinator.Service.TryGetLastSuccessfulLiveResult(provider, out var lastSuccess))
                return lastSuccess;
            if (coordinator.Service.Get(provider) is { } usageProvider)
                return UsageResult.Pending(provider, usageProvider, "Loading...");
            return null;
        }

        /// <summary>
        /// Keeps pinned (non-active) tiles fresh. The coordinator's tick only fetches the active provider,
        /// so a pinned tile would otherwise freeze on its boot snapshot. The fetch is cache-TTL gated, so
        /// on most ticks this is a cache hit.
        /// </summary>
        private static void RefreshPinnedTiles()
        {
            var coordinator = UsageCoordinator.Instance;
            IEnumerable<ProviderId> providers = coordinator.WidgetDisplayProviders;
            if (WidgetSettingsService.CurrentTaskbarPlacement == TaskbarPlacementMode.Adaptive)
                providers = providers.Concat(AdaptiveDisplayProviders.Providers).Distinct();

            foreach (var provider in providers)
            {
                if (provider != coordinator.ActiveProvider)
                    _ = coordinator.RefreshWidgetProviderAsync(provider);
            }
        }

        private static void ToggleFlyout(TaskBarWidget widget)
        {
            if (!widget.IsAlive) return;
            ToggleFlyout(widget.Handle);
        }

        private static void ToggleFlyout(IntPtr anchorHandle)
        {
            if (anchorHandle == IntPtr.Zero) return;
            FlyoutWindow? flyout = null;
            try
            {
                flyout = _flyout ?? new FlyoutWindow();
                _flyout = flyout;
                flyout.ToggleAbove(anchorHandle);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to toggle flyout");
                try { flyout?.Close(); } catch { }
                _flyout = null;
            }
        }

        private static void ToggleActivityFlyout(TaskBarWidget? sourceWidget, string? selectedActivityId)
        {
            if (sourceWidget is { IsAlive: true })
            {
                ToggleActivityFlyout(sourceWidget.ActivityHandle, selectedActivityId);
                return;
            }

            ToggleActivityFlyout(anchorHandle: null, selectedActivityId);
        }

        private static void ToggleActivityFlyout(IntPtr? anchorHandle, string? selectedActivityId)
        {
            IntPtr handle = anchorHandle is { } h && h != IntPtr.Zero
                ? h
                : IsFloatingSurface
                    ? _floatingWindow?.Handle ?? IntPtr.Zero
                    : PrimaryWidget() is { IsAlive: true } primary
                        ? primary.ActivityHandle
                        : IntPtr.Zero;

            if (handle == IntPtr.Zero)
            {
                Log.Warning("Cannot toggle agent activity: no usage surface is available");
                return;
            }

            FlyoutWindow? flyout = null;
            try
            {
                flyout = _flyout ?? new FlyoutWindow();
                _flyout = flyout;
                flyout.ToggleActivityAbove(handle, selectedActivityId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to toggle agent activity flyout");
                try { flyout?.Close(); } catch { }
                _flyout = null;
            }
        }

        private static void PrewarmFlyout()
        {
            _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                FlyoutWindow? flyout = null;
                try
                {
                    flyout = _flyout ?? new FlyoutWindow();
                    _flyout = flyout;
                    flyout.Prewarm();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to prewarm flyout");
                    try { flyout?.Close(); } catch { }
                    _flyout = null;
                }
            });
        }

        private static void ScheduleFloatingPrewarm()
        {
            if (_dispatcher is null || IsFloatingSurface || WidgetSettingsService.CurrentSurface != WidgetSurfaceMode.Taskbar)
                return;

            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (IsFloatingSurface || WidgetSettingsService.CurrentSurface != WidgetSurfaceMode.Taskbar)
                    return;

                try
                {
                    EnsureFloatingWindow();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to prewarm floating usage window");
                    try { _floatingWindow?.Close(); } catch { }
                    _floatingWindow = null;
                }
            });
        }

        private static void OnStateChanged(UsageResult result)
            => _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () => ApplyStateChanged(result));

        private static void ApplyStateChanged(UsageResult result)
        {
            var coordinator = UsageCoordinator.Instance;
            var allProviders = coordinator.WidgetDisplayProviders;
            var activity = AgentActivityService.Instance.Snapshot;
            bool isDisplayed = allProviders.Contains(result.Id);

            if (IsFloatingSurface)
            {
                if (!SyncFloatingSurface() || !isDisplayed)
                    return;

                _floatingWindow!.ApplyResult(result);
                LogWidgetApply(result.Id, "floating-state");
                return;
            }

            // Reused buffer: this runs on every usage publish, so a fresh array per publish was pure waste.
            SnapshotWidgets();
            foreach (var widget in _widgetBuffer)
            {
                if (!widget.IsAlive)
                    continue;

                var providers = ProvidersForWidget(widget, allProviders);
                var routedActivity = ActivityForWidget(widget, activity);
                bool isDisplayedOnWidget = providers.Contains(result.Id);

                // Reconcile the tile set first, so a provider that just became active already owns a slot
                // before its result is routed. SetDisplayProviders is a cheap no-op when nothing changed.
                widget.SetActivitySnapshot(routedActivity, allowEmptyGrace: activity.Primary is null);
                // Keep the outgoing slots bound until their fade completes; the widget clears them in the
                // completion callback, guarded so an interrupted hide cannot erase a returning provider.
                if (providers.Count == 0)
                {
                    widget.HideDisplayProviders(
                        ActiveProviderForWidget(widget, coordinator.ActiveProvider),
                        keepActivityVisible: widget.HasVisibleActivity);
                    continue;
                }

                widget.SetDisplayProviders(providers, ActiveProviderForWidget(widget, coordinator.ActiveProvider));
                widget.SetVisible(true);
                if (!isDisplayedOnWidget)
                    continue;

                widget.ApplyResult(result);
                LogWidgetApply(result.Id, "state");
            }
        }

        /// <summary>
        /// Refills <see cref="_widgetBuffer"/> from the live dictionary. Callers iterate the buffer rather
        /// than the dictionary because a widget callback can remove an entry mid-loop; the buffer replaces
        /// the defensive copy that the hot paths used to allocate per pass. UI thread only, and the buffer
        /// stays valid only until the next call.
        /// </summary>
        private static void SnapshotWidgets()
        {
            _widgetBuffer.Clear();
            foreach (var widget in Widgets.Values)
                _widgetBuffer.Add(widget);
        }

        private static void LogWidgetApply(ProviderId provider, string source)
        {
            if (_lastLoggedWidgetApplyProvider == provider)
                return;

            _lastLoggedWidgetApplyProvider = provider;
            Log.Debug($"[synara] widget {source} applied provider={provider}");
        }

        // Foreground provider detection is strictly for quota context. Agent activity is refreshed by
        // its own desktop-app and terminal-command scans, even when another app has the focus.
        private static void OnActiveProviderChanged(ProviderId? _)
            => _dispatcher?.TryEnqueue(SyncWidgetState);

        /// <summary>
        /// Fired from the WinEvent hook the instant the foreground window changes. Relays to the
        /// coordinator so it can re-detect whether a provider is in front without waiting for the
        /// next 500 ms tick — this is what makes "switch away" hide the widget right away.
        /// </summary>
        private static void OnForegroundChanged(IntPtr _)
            => UsageCoordinator.Instance.NotifyForegroundChanged();

        private static void OnProviderWindowObserved(ProviderId provider, IntPtr hwnd)
        {
            string displayKey = TaskbarWindowTarget.GetDisplayKeyForWindow(hwnd);
            if (displayKey.Length == 0)
                return;

            _dispatcher?.TryEnqueue(() => RecordAdaptiveProviderDisplay(provider, displayKey, hwnd));
        }

        private static void OnWindowMoveSizeEnded(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || User32.GetForegroundWindow() != hwnd)
                return;
            if (UsageCoordinator.Instance.ActiveProvider is not { } provider)
                return;

            string displayKey = TaskbarWindowTarget.GetDisplayKeyForWindow(hwnd);
            if (displayKey.Length != 0)
                RecordAdaptiveProviderDisplay(provider, displayKey, hwnd);
        }

        private static void RecordAdaptiveProviderDisplay(ProviderId provider, string displayKey, IntPtr hwnd)
        {
            bool activeChanged = AdaptiveDisplayProviders.Observe(provider, displayKey, hwnd);
            bool destinationChanged = WidgetSettingsService.SetAdaptiveProviderDisplay(provider, displayKey);
            if (!activeChanged && !destinationChanged)
                return;

            if (destinationChanged)
                Log.Information($"Adaptive placement: provider={provider}, display={displayKey}");
            if (!IsFloatingSurface
                && WidgetSettingsService.CurrentTaskbarPlacement == TaskbarPlacementMode.Adaptive)
            {
                SyncWidgetState();
            }
        }

        private static void OnActiveToolPresenceChanged(bool isPresent)
            => _dispatcher?.TryEnqueue(() => ApplyActiveToolPresenceChanged(isPresent));

        // Focus-follows-provider flip (opt-in setting): the tile set changes even though presence and the
        // active provider did not, so the widget has to be re-synced from the recomputed set.
        private static void OnProviderForegroundChanged(bool _)
            => _dispatcher?.TryEnqueue(SyncWidgetState);

        private static void ApplyActiveToolPresenceChanged(bool isPresent)
        {
            // Pinned tiles stay on the surface even when no AI tool is in the foreground; only the active
            // tile follows presence. SyncWidgetState recomputes the whole set and hydrates it.
            SyncWidgetState();
        }

        private static void OnActivityChanged(AgentActivitySnapshot _)
            => _dispatcher?.TryEnqueue(SyncWidgetState);

        private static void OnWidgetSettingsChanged(object? sender, EventArgs e)
        {
            _dispatcher?.TryEnqueue(() =>
            {
                ConfigureActivityTimer();
                if (WidgetSettingsService.CurrentSurface != _activeSurface)
                    ApplySurfaceFromSettings();
                else
                    SyncWidgetState();
            });
        }

        private static void OnQuitting()
        {
            UsageCoordinator.Instance.StateChanged -= OnStateChanged;
            UsageCoordinator.Instance.ActiveProviderChanged -= OnActiveProviderChanged;
            UsageCoordinator.Instance.ActiveToolPresenceChanged -= OnActiveToolPresenceChanged;
            UsageCoordinator.Instance.ProviderWindowObserved -= OnProviderWindowObserved;
            AgentActivityService.Instance.Changed -= OnActivityChanged;
            UsageCoordinator.Instance.ProviderForegroundChanged -= OnProviderForegroundChanged;
            UsageCoordinator.Instance.IsOwnUiEngaged = null;
            WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
            _initialized = false;
            _widgetHealthTimer?.Stop();
            _widgetHealthTimer = null;
            _topologyRecoveryTimer?.Stop();
            _topologyRecoveryTimer = null;
            _topologyRecoveryPending = false;
            _topologyForceReset = false;
            _topologyRecoveryAttempts = 0;
            _topologyRecoveryReason = string.Empty;
            TopologyStability.Reset();
            MissingTaskbarObservations.Clear();
            AdaptiveDisplayProviders.Clear();
            _activityTimer?.Stop();
            _activityTimer = null;
            _activityCts?.Cancel();
            _activityCts?.Dispose();
            _activityCts = null;
            if (_foregroundWatcher is { } watcher)
            {
                watcher.ForegroundChanged -= OnForegroundChanged;
                watcher.WindowMoveSizeEnded -= OnWindowMoveSizeEnded;
                watcher.Dispose();
                _foregroundWatcher = null;
            }
            if (_sessionTopologyWatcher is { } topologyWatcher)
            {
                topologyWatcher.Changed -= OnTopologyChanged;
                topologyWatcher.Dispose();
                _sessionTopologyWatcher = null;
            }
            if (_trayIcon != null) { _trayIcon.TryRemove(); _trayIcon.Dispose(); _trayIcon = null; }
            try { _flyout?.Close(); } catch { }
            _flyout = null;
            DisposeFloatingWindow();
            DisposeAllTaskbarWidgets();
            _showMainWindow = null;
        }
    }
}
