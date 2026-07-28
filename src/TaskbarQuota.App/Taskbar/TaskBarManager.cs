using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Taskbar
{
    /// <summary>Owns the tray icon and injected taskbar widgets, and pushes coordinator state into every widget.</summary>
    internal static class TaskBarManager
    {
        private static TrayIconWithContextMenu? _trayIcon;
        private static System.Drawing.Icon? _trayIconSource;
        private static readonly Dictionary<IntPtr, TaskBarWidget> Widgets = new();
        // Reused snapshot of Widgets.Values, so iterating it while a callback may mutate the dictionary
        // doesn't allocate. Only valid until the next SnapshotWidgets call, and only used on the UI thread.
        private static readonly List<TaskBarWidget> _widgetBuffer = new();
        private static FlyoutWindow? _flyout;
        private static DispatcherQueue? _dispatcher;
        private static Action? _showMainWindow;
        private static DispatcherTimer? _widgetHealthTimer;
        private static DispatcherTimer? _visibilityTimer;
        private static bool _initialized;
        private static bool _isReconcilingWidgets;
        private static ProviderId? _lastLoggedWidgetApplyProvider;
        private static PopupMenuItem? _showWidgetMenuItem;
        private static PopupMenuItem? _hideWidgetMenuItem;
        private static PopupMenuItem? _automaticWidgetMenuItem;
        private static PopupMenuItem? _moveWidgetMenuItem;
        private static WidgetVisibilityOverride _visibilityOverride = WidgetVisibilityOverride.Automatic;
        private static readonly WidgetVisibilityStabilizer VisibilityStabilizer = new();
        private static WidgetVisibilityDecision? _lastVisibilityDecision;
        private static bool _lastWidgetVisible;
        private static WidgetVisibilityMode? _lastObservedVisibilityMode;
        private static bool? _lastObservedBackgroundAgent;

        public static void Initialize(DispatcherQueue dispatcher, Action showMainWindow)
        {
            _dispatcher = dispatcher;
            _showMainWindow = showMainWindow;
            _lastObservedVisibilityMode = WidgetSettingsService.CurrentVisibilityMode;
            _lastObservedBackgroundAgent = WidgetSettingsService.KeepVisibleWhileBackgroundAgentRunning;

            CreateTrayIcon();
            EnsureWidgets();

            if (!_initialized)
            {
                UsageCoordinator.Instance.StateChanged += OnStateChanged;
                UsageCoordinator.Instance.ActiveProviderChanged += OnActiveProviderChanged;
                UsageCoordinator.Instance.ActiveToolPresenceChanged += OnActiveToolPresenceChanged;
                UsageCoordinator.Instance.SupportedSurfacesChanged += OnSupportedSurfacesChanged;
                WidgetSettingsService.Changed += OnWidgetSettingsChanged;
                App.Quitting += OnQuitting;
                _initialized = true;
            }

            StartWidgetHealthTimer();
            SyncWidgetState();
        }

        private static void CreateTrayIcon()
        {
            var open = new PopupMenuItem("Open TaskbarQuota", (_, _) => _dispatcher?.TryEnqueue(() => _showMainWindow?.Invoke()));
            _showWidgetMenuItem = new PopupMenuItem("Show widget", (_, _) => _dispatcher?.TryEnqueue(
                () => ApplyVisibilityOverride(WidgetVisibilityOverride.ForceShow)));
            _hideWidgetMenuItem = new PopupMenuItem("Hide widget", (_, _) => _dispatcher?.TryEnqueue(
                () => ApplyVisibilityOverride(WidgetVisibilityOverride.ForceHide)));
            _automaticWidgetMenuItem = new PopupMenuItem("Use automatic visibility", (_, _) => _dispatcher?.TryEnqueue(
                () => ApplyVisibilityOverride(WidgetVisibilityOverride.Automatic)));
            var visibility = new PopupSubMenu("Widget visibility")
            {
                Items = { _showWidgetMenuItem, _hideWidgetMenuItem, _automaticWidgetMenuItem },
            };
            _moveWidgetMenuItem = new PopupMenuItem("Move primary taskbar widget", (_, _) => _dispatcher?.TryEnqueue(
                () => PrimaryWidget()?.StartDragging()));
            var reset = new PopupMenuItem("Reset widget positions", (_, _) => _dispatcher?.TryEnqueue(
                () =>
                {
                    foreach (var widget in Widgets.Values.ToArray())
                        widget.UpdatePosition(resetManualPosition: true);
                }));
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
                ContextMenu = new PopupMenu
                {
                    Items =
                    {
                        open,
                        new PopupMenuSeparator(),
                        visibility,
                        _moveWidgetMenuItem,
                        reset,
                        new PopupMenuSeparator(),
                        quit,
                    },
                },
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

        private static void StartWidgetHealthTimer()
        {
            if (_widgetHealthTimer != null)
                return;

            _widgetHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _widgetHealthTimer.Tick += (_, _) =>
            {
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

            _visibilityTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _visibilityTimer.Tick -= OnVisibilityTimerTick;
            _visibilityTimer.Tick += OnVisibilityTimerTick;
        }

        private static void EnsureWidgets()
        {
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
                    if (targetsByHandle.TryGetValue(pair.Key, out var target)
                        && pair.Value.IsAlive
                        // A window whose host content was never built renders nothing and cannot recover on
                        // its own — it is dead in every way that matters to the user, so recreate it.
                        && pair.Value.IsHostContentReady
                        && pair.Value.IsDpiCurrent
                        && pair.Value.MatchesTarget(target))
                    {
                        continue;
                    }

                    Widgets.Remove(pair.Key);
                    Log.Warning($"Taskbar widget, target taskbar, or DPI changed; recreating taskbar=0x{pair.Key.ToInt64():X}");
                    try { pair.Value.Dispose(); }
                    catch (Exception ex) { Log.Warning(ex, "Failed to dispose missing taskbar widget"); }
                }

                foreach (var target in targets)
                {
                    if (!Widgets.ContainsKey(target.Handle))
                        CreateWidget(target);
                }
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

        private static void SyncWidgetState(bool hideImmediately = false, bool hydrate = true)
        {
            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;
            var decision = EvaluateVisibilityDecision(providers, hideImmediately);
            bool visible = decision.ShouldShowWidget && providers.Count > 0;
            bool needsFetch = false;

            foreach (var widget in Widgets.Values.ToArray())
            {
                if (!widget.IsAlive)
                    continue;

                if (visible)
                {
                    // Bind before exposing the native host so a show never paints one frame of stale or
                    // collapsed content.
                    widget.SetDisplayProviders(providers, coordinator.ActiveWidgetProvider);
                    widget.SetVisible(true);
                }
                else
                {
                    // Leave the current tiles bound during the short host fade. Collapsing them first makes
                    // the animation look like a blank flash.
                    widget.SetVisible(false);
                }

                if (!visible || !hydrate)
                    continue;

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
            }

            FinalizeVisibilityDecision(decision, visible);
            if (needsFetch)
                _ = coordinator.TickAsync(force: true);
        }

        private static void SyncWidgetState(TaskBarWidget widget)
        {
            if (!widget.IsAlive)
                return;

            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;
            var decision = EvaluateVisibilityDecision(providers);
            bool visible = decision.ShouldShowWidget && providers.Count > 0;
            if (visible)
            {
                widget.SetDisplayProviders(providers, coordinator.ActiveWidgetProvider);
                widget.SetVisible(true);
            }
            else
            {
                widget.SetVisible(false);
            }

            bool needsFetch = false;
            if (visible)
            {
                foreach (var provider in providers)
                {
                    var toApply = HydrateResult(coordinator, provider);
                    if (toApply is { } result)
                    {
                        widget.ApplyResult(result, force: true);
                        LogWidgetApply(result.Id, "sync");
                    }

                    if (toApply is null or { Ok: false })
                        needsFetch = true;
                }
            }

            FinalizeVisibilityDecision(decision, visible);
            if (needsFetch)
                _ = coordinator.TickAsync(force: true);
        }

        /// <summary>
        /// Applies only the native host visibility decision. Focus/presence transitions do not change the
        /// usage snapshot, so routing them through <see cref="SyncWidgetState(bool, bool)"/> needlessly
        /// re-runs tile layout and render animations throughout the hide debounce.
        /// </summary>
        private static void SyncWidgetVisibility()
        {
            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;
            var decision = EvaluateVisibilityDecision(providers);
            bool visible = decision.ShouldShowWidget && providers.Count > 0;

            // A newly visible host may have missed provider updates while hidden. Perform one full bind
            // before showing it; transitions that keep it visible do no content work at all.
            if (visible && !_lastWidgetVisible)
            {
                SyncWidgetState();
                return;
            }

            foreach (var widget in Widgets.Values.ToArray())
            {
                if (widget.IsAlive)
                    widget.SetVisible(visible);
            }

            FinalizeVisibilityDecision(decision, visible);
        }

        private static WidgetVisibilityDecision EvaluateVisibilityDecision(
            IReadOnlyList<ProviderId> providers,
            bool hideImmediately = false)
        {
            var input = new WidgetVisibilityInput(
                WidgetSettingsService.CurrentVisibilityMode,
                _visibilityOverride,
                providers.Count > 0,
                WidgetSettingsService.KeepVisibleWhileBackgroundAgentRunning,
                UsageCoordinator.Instance.SupportedSurfaces);
            var raw = WidgetVisibilityPolicy.Evaluate(input);
            bool bypassDelay = hideImmediately
                || raw.Reason is WidgetVisibilityReason.ManualForceHide
                    or WidgetVisibilityReason.NoValidProvider;
            return VisibilityStabilizer.Apply(raw, DateTimeOffset.UtcNow, bypassDelay);
        }

        private static void FinalizeVisibilityDecision(WidgetVisibilityDecision decision, bool visible)
        {
            if (_lastWidgetVisible && !visible)
                CloseFlyout();

            _lastWidgetVisible = visible;
            UpdateTrayMenuState();

            if (decision.Reason == WidgetVisibilityReason.HideDebounce)
                _visibilityTimer?.Start();
            else
                _visibilityTimer?.Stop();

            if (_lastVisibilityDecision != decision)
            {
                _lastVisibilityDecision = decision;
                var provider = UsageCoordinator.Instance.WidgetDisplayProvider?.ToString() ?? "none";
                Log.Information(
                    $"[visibility] visible={visible} reason={decision.Reason} " +
                    $"policy={WidgetSettingsService.CurrentVisibilityMode} provider={provider}");
            }
        }

        private static void OnVisibilityTimerTick(object? sender, object e)
            => SyncWidgetVisibility();

        private static void ApplyVisibilityOverride(WidgetVisibilityOverride visibilityOverride)
        {
            if (_visibilityOverride == visibilityOverride)
                return;

            _visibilityOverride = visibilityOverride;
            Log.Information($"[visibility] override={visibilityOverride}");
            SyncWidgetState(hideImmediately: true);
        }

        private static void UpdateTrayMenuState()
        {
            if (_showWidgetMenuItem is not null)
                _showWidgetMenuItem.Checked = _visibilityOverride == WidgetVisibilityOverride.ForceShow;
            if (_hideWidgetMenuItem is not null)
                _hideWidgetMenuItem.Checked = _visibilityOverride == WidgetVisibilityOverride.ForceHide;
            if (_automaticWidgetMenuItem is not null)
                _automaticWidgetMenuItem.Checked = _visibilityOverride == WidgetVisibilityOverride.Automatic;
            if (_moveWidgetMenuItem is not null)
                _moveWidgetMenuItem.Enabled = _lastWidgetVisible;
        }

        private static void CloseFlyout()
        {
            try { _flyout?.Close(); } catch { }
            _flyout = null;
        }

        /// <summary>
        /// Best snapshot available to seed a tile: the last active publish, then either cache tier, then a
        /// Pending placeholder. Null only when the provider is unknown to the usage service.
        /// </summary>
        private static UsageResult? HydrateResult(UsageCoordinator coordinator, ProviderId provider)
        {
            if (coordinator.LastState is { } last && last.Id == provider)
                return last;
            if (coordinator.Service.TryGetCached(provider, out var cached))
                return cached;
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
            foreach (var provider in coordinator.WidgetDisplayProviders)
            {
                if (provider != coordinator.ActiveProvider)
                    _ = coordinator.RefreshWidgetProviderAsync(provider);
            }
        }

        private static void ToggleFlyout(TaskBarWidget widget)
        {
            if (!widget.IsAlive) return;
            try
            {
                _flyout ??= new FlyoutWindow();
                _flyout.ToggleAbove(widget.Handle);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to toggle flyout");
                _flyout = null;
            }
        }

        private static void PrewarmFlyout()
        {
            _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    _flyout ??= new FlyoutWindow();
                    _flyout.Prewarm();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to prewarm flyout");
                    _flyout = null;
                }
            });
        }

        private static void OnStateChanged(UsageResult result)
            => _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () => ApplyStateChanged(result));

        private static void ApplyStateChanged(UsageResult result)
        {
            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;
            bool isDisplayed = providers.Contains(result.Id);
            var decision = EvaluateVisibilityDecision(providers);
            bool visible = decision.ShouldShowWidget && providers.Count > 0;

            // Reused buffer: this runs on every usage publish, so a fresh array per publish was pure waste.
            SnapshotWidgets();
            foreach (var widget in _widgetBuffer)
            {
                if (!widget.IsAlive)
                    continue;

                // Reconcile the tile set first, so a provider that just became active already owns a slot
                // before its result is routed. SetDisplayProviders is a cheap no-op when nothing changed.
                if (!visible)
                {
                    widget.SetVisible(false);
                    continue;
                }

                widget.SetDisplayProviders(providers, coordinator.ActiveWidgetProvider);
                widget.SetVisible(true);
                if (!isDisplayed)
                    continue;

                widget.ApplyResult(result);
                LogWidgetApply(result.Id, "state");
            }

            FinalizeVisibilityDecision(decision, visible);
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

        // Provider switches always publish StateChanged immediately after ActiveProviderChanged,
        // so updating the widget here would only duplicate dispatcher work and add latency.
        private static void OnActiveProviderChanged(ProviderId? _) { }

        private static void OnActiveToolPresenceChanged(bool isPresent)
            => _dispatcher?.TryEnqueue(() => ApplyActiveToolPresenceChanged(isPresent));

        private static void ApplyActiveToolPresenceChanged(bool _)
            // Like SupportedSurfacesChanged, this is a visibility transition rather than new usage data.
            // SyncWidgetVisibility performs a full bind when the host becomes visible, but avoids forcing a
            // tile re-render while it remains visible during the hide debounce.
            => SyncWidgetVisibility();

        private static void OnSupportedSurfacesChanged(SupportedSurfaceState _)
            => _dispatcher?.TryEnqueue(SyncWidgetVisibility);

        private static void OnWidgetSettingsChanged(object? sender, EventArgs e)
        {
            _dispatcher?.TryEnqueue(() =>
            {
                var mode = WidgetSettingsService.CurrentVisibilityMode;
                var backgroundAgent = WidgetSettingsService.KeepVisibleWhileBackgroundAgentRunning;
                bool visibilitySettingChanged = _lastObservedVisibilityMode != mode
                    || _lastObservedBackgroundAgent != backgroundAgent;
                _lastObservedVisibilityMode = mode;
                _lastObservedBackgroundAgent = backgroundAgent;
                if (visibilitySettingChanged)
                    Log.Information($"[visibility] policy={mode} backgroundAgent={backgroundAgent}");
                SyncWidgetState(hideImmediately: visibilitySettingChanged);
            });
        }

        private static void OnQuitting()
        {
            UsageCoordinator.Instance.StateChanged -= OnStateChanged;
            UsageCoordinator.Instance.ActiveProviderChanged -= OnActiveProviderChanged;
            UsageCoordinator.Instance.ActiveToolPresenceChanged -= OnActiveToolPresenceChanged;
            UsageCoordinator.Instance.SupportedSurfacesChanged -= OnSupportedSurfacesChanged;
            WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
            _initialized = false;
            _widgetHealthTimer?.Stop();
            _widgetHealthTimer = null;
            _visibilityTimer?.Stop();
            _visibilityTimer = null;
            if (_trayIcon != null) { _trayIcon.TryRemove(); _trayIcon.Dispose(); _trayIcon = null; }
            CloseFlyout();
            foreach (var widget in Widgets.Values.ToArray())
            {
                try { widget.Dispose(); } catch { }
            }
            Widgets.Clear();
            _showWidgetMenuItem = null;
            _hideWidgetMenuItem = null;
            _automaticWidgetMenuItem = null;
            _moveWidgetMenuItem = null;
            _visibilityOverride = WidgetVisibilityOverride.Automatic;
            _lastVisibilityDecision = null;
            _lastWidgetVisible = false;
            _lastObservedVisibilityMode = null;
            _lastObservedBackgroundAgent = null;
            VisibilityStabilizer.Reset(isVisible: false);
        }
    }
}
