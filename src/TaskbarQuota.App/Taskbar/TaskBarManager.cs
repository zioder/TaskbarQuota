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
        private static bool _initialized;
        private static bool _isReconcilingWidgets;
        private static ProviderId? _lastLoggedWidgetApplyProvider;

        public static void Initialize(DispatcherQueue dispatcher, Action showMainWindow)
        {
            _dispatcher = dispatcher;
            _showMainWindow = showMainWindow;

            CreateTrayIcon();
            EnsureWidgets();

            if (!_initialized)
            {
                UsageCoordinator.Instance.StateChanged += OnStateChanged;
                UsageCoordinator.Instance.ActiveProviderChanged += OnActiveProviderChanged;
                UsageCoordinator.Instance.ActiveToolPresenceChanged += OnActiveToolPresenceChanged;
                WidgetSettingsService.Changed += OnWidgetSettingsChanged;
                App.Quitting += OnQuitting;
                _initialized = true;
            }

            StartWidgetHealthTimer();
            OnActiveToolPresenceChanged(UsageCoordinator.Instance.IsActiveToolPresent);
        }

        private static void CreateTrayIcon()
        {
            var open = new PopupMenuItem("Open TaskbarQuota", (_, _) => _dispatcher?.TryEnqueue(() => _showMainWindow?.Invoke()));
            var move = new PopupMenuItem("Move primary taskbar widget", (_, _) => _dispatcher?.TryEnqueue(
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
                ContextMenu = new PopupMenu { Items = { open, new PopupMenuSeparator(), move, reset, new PopupMenuSeparator(), quit } },
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

        private static void SyncWidgetState()
        {
            foreach (var widget in Widgets.Values.ToArray())
                SyncWidgetState(widget);
        }

        private static void SyncWidgetState(TaskBarWidget widget)
        {
            if (!widget.IsAlive)
                return;

            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;

            // No provider to show -> hide the native host instead of leaving a transparent taskbar child
            // window over the notification area (#10).
            widget.SetVisible(providers.Count > 0);
            widget.SetDisplayProviders(providers, coordinator.ActiveProvider);
            if (providers.Count == 0)
                return;

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

            // Reused buffer: this runs on every usage publish, so a fresh array per publish was pure waste.
            SnapshotWidgets();
            foreach (var widget in _widgetBuffer)
            {
                if (!widget.IsAlive)
                    continue;

                // Reconcile the tile set first, so a provider that just became active already owns a slot
                // before its result is routed. SetDisplayProviders is a cheap no-op when nothing changed.
                widget.SetVisible(providers.Count > 0);
                widget.SetDisplayProviders(providers, coordinator.ActiveProvider);
                if (!isDisplayed)
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

        // Provider switches always publish StateChanged immediately after ActiveProviderChanged,
        // so updating the widget here would only duplicate dispatcher work and add latency.
        private static void OnActiveProviderChanged(ProviderId? _) { }

        private static void OnActiveToolPresenceChanged(bool isPresent)
            => _dispatcher?.TryEnqueue(() => ApplyActiveToolPresenceChanged(isPresent));

        private static void ApplyActiveToolPresenceChanged(bool isPresent)
        {
            // Pinned tiles stay on the taskbar even when no AI tool is in the foreground; only the active
            // tile follows presence. SyncWidgetState recomputes the whole set and hydrates it.
            foreach (var widget in Widgets.Values.ToArray())
            {
                if (widget.IsAlive)
                    SyncWidgetState(widget);
            }
        }

        private static void OnWidgetSettingsChanged(object? sender, EventArgs e)
        {
            _dispatcher?.TryEnqueue(SyncWidgetState);
        }

        private static void OnQuitting()
        {
            UsageCoordinator.Instance.StateChanged -= OnStateChanged;
            UsageCoordinator.Instance.ActiveProviderChanged -= OnActiveProviderChanged;
            UsageCoordinator.Instance.ActiveToolPresenceChanged -= OnActiveToolPresenceChanged;
            WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
            _initialized = false;
            _widgetHealthTimer?.Stop();
            _widgetHealthTimer = null;
            if (_trayIcon != null) { _trayIcon.TryRemove(); _trayIcon.Dispose(); _trayIcon = null; }
            try { _flyout?.Close(); } catch { }
            _flyout = null;
            foreach (var widget in Widgets.Values.ToArray())
            {
                try { widget.Dispose(); } catch { }
            }
            Widgets.Clear();
        }
    }
}
