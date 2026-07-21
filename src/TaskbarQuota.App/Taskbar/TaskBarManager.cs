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
            _widgetHealthTimer.Tick += (_, _) => EnsureWidgets();
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
                if (widget.Summary is { } summary)
                    summary.Clicked += () => _dispatcher?.TryEnqueue(() => ToggleFlyout(widget));
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
            if (!widget.IsAlive || widget.Summary is not { } summary)
                return;

            var coordinator = UsageCoordinator.Instance;
            var target = coordinator.WidgetDisplayProvider;
            bool shouldShowWidget = coordinator.IsActiveToolPresent && target is not null;
            widget.SetVisible(shouldShowWidget);

            // No enabled+available provider -> hide the native host instead of leaving a transparent
            // taskbar child window over the notification area (#10).
            if (target is not { } targetProvider)
            {
                summary.SetActiveToolVisible(false);
                return;
            }

            UsageResult? toApply = coordinator.LastState is { } last && last.Id == targetProvider
                ? last
                : coordinator.Service.TryGetCached(targetProvider, out var cached)
                    ? cached
                    : coordinator.Service.TryGetLastSuccessfulLiveResult(targetProvider, out var lastSuccess)
                        ? lastSuccess
                        : coordinator.Service.Get(targetProvider) is { } usageProvider
                            ? UsageResult.Pending(targetProvider, usageProvider, "Loading...")
                            : null;

            if (toApply is { } result)
            {
                summary.Apply(result, force: true);
                LogWidgetApply(result.Id, "sync");
            }

            summary.SetActiveToolVisible(shouldShowWidget);

            if (toApply is null)
                _ = coordinator.TickAsync(force: true);
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
            var active = UsageCoordinator.Instance.WidgetDisplayProvider;
            if (active is null || result.Id != active)
                return;

            foreach (var widget in Widgets.Values.ToArray())
            {
                if (!widget.IsAlive || widget.Summary is not { } summary)
                    continue;

                summary.Apply(result);
                LogWidgetApply(result.Id, "state");
                bool isVisible = UsageCoordinator.Instance.IsActiveToolPresent
                    && UsageCoordinator.Instance.WidgetDisplayProvider is not null;
                widget.SetVisible(isVisible);
                summary.SetActiveToolVisible(isVisible);
            }
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
            bool isVisible = isPresent && UsageCoordinator.Instance.WidgetDisplayProvider is not null;
            foreach (var widget in Widgets.Values.ToArray())
            {
                if (!widget.IsAlive || widget.Summary is not { } summary)
                    continue;

                widget.SetVisible(isVisible);
                summary.SetActiveToolVisible(isVisible);
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
