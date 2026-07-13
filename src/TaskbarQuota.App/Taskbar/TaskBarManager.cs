using System;
using System.IO;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Taskbar
{
    /// <summary>Owns the tray icon and the injected taskbar widget, and pushes coordinator state into the widget.</summary>
    internal static class TaskBarManager
    {
        private static TrayIconWithContextMenu? _trayIcon;
        private static System.Drawing.Icon? _trayIconSource;
        private static TaskBarWidget? _widget;
        private static FlyoutWindow? _flyout;
        private static DispatcherQueue? _dispatcher;
        private static Action? _showMainWindow;
        private static DispatcherTimer? _widgetHealthTimer;
        private static bool _initialized;
        private static bool _isCreatingWidget;
        private static ProviderId? _lastLoggedWidgetApplyProvider;

        public static void Initialize(DispatcherQueue dispatcher, Action showMainWindow)
        {
            _dispatcher = dispatcher;
            _showMainWindow = showMainWindow;

            CreateTrayIcon();
            EnsureWidget();

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
            var open = new PopupMenuItem("Open TaskbarQuota dashboard", (_, _) => _dispatcher?.TryEnqueue(() => _showMainWindow?.Invoke()));
            var move = new PopupMenuItem("Move taskbar widget", (_, _) => _dispatcher?.TryEnqueue(() => _widget?.StartDragging()));
            var reset = new PopupMenuItem("Reset widget position", (_, _) => _dispatcher?.TryEnqueue(() => _widget?.UpdatePosition(resetManualPosition: true)));
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
                if (e.MouseEvent == MouseEvent.IconLeftMouseUp)
                    _dispatcher?.TryEnqueue(ToggleTrayFlyout);
            };
        }

        private static void StartWidgetHealthTimer()
        {
            if (_widgetHealthTimer != null)
                return;

            _widgetHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _widgetHealthTimer.Tick += (_, _) => EnsureWidget();
            _widgetHealthTimer.Start();
        }

        private static void EnsureWidget()
        {
            if (_isCreatingWidget)
                return;

            if (_widget is { } widget)
            {
                if (widget.IsAlive)
                    return;

                Log.Warning("Taskbar widget window disappeared; recreating");
                try { widget.Dispose(); } catch (Exception ex) { Log.Warning(ex, "Failed to dispose missing taskbar widget"); }
                _widget = null;
            }

            ShowWidget();
        }

        private static void ShowWidget()
        {
            if (_widget != null) return;
            _isCreatingWidget = true;
            try
            {
                var widget = new TaskBarWidget();
                widget.Initialize();
                widget.Destroying += (_, _) => _widget = null;
                if (widget.Summary is { } summary)
                    summary.Clicked += () => _dispatcher?.TryEnqueue(ToggleFlyout);
                _widget = widget;
                SyncWidgetState();
                PrewarmFlyout();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create taskbar widget");
            }
            finally
            {
                _isCreatingWidget = false;
            }
        }

        private static void SyncWidgetState()
        {
            var widget = _widget;
            if (widget?.Summary is not { } summary)
                return;

            var coordinator = UsageCoordinator.Instance;
            summary.DispatcherQueue.TryEnqueue(() =>
            {
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
            });
        }

        private static void ToggleFlyout()
        {
            if (_widget is null) return;
            try
            {
                _flyout ??= new FlyoutWindow();
                _flyout.ToggleAbove(_widget.Handle);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to toggle flyout");
                _flyout = null;
            }
        }

        private static void ToggleTrayFlyout()
        {
            try
            {
                _flyout ??= new FlyoutWindow();
                _flyout.ToggleAtTrayIcon();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to toggle tray flyout");
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
        {
            var widget = _widget;
            if (widget?.Summary is null) return;

            var active = UsageCoordinator.Instance.WidgetDisplayProvider;
            if (active is null || result.Id != active)
                return;

            widget.Summary.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () =>
            {
                widget.Summary.Apply(result);
                LogWidgetApply(result.Id, "state");
                bool isVisible = UsageCoordinator.Instance.IsActiveToolPresent
                    && UsageCoordinator.Instance.WidgetDisplayProvider is not null;
                widget.SetVisible(isVisible);
                widget.Summary.SetActiveToolVisible(isVisible);
            });
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
        {
            var widget = _widget;
            if (widget?.Summary is null) return;
            bool isVisible = isPresent && UsageCoordinator.Instance.WidgetDisplayProvider is not null;
            widget.Summary.DispatcherQueue.TryEnqueue(() =>
            {
                widget.SetVisible(isVisible);
                widget.Summary.SetActiveToolVisible(isVisible);
            });
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
            _widget?.Dispose();
            _widget = null;
        }
    }
}
