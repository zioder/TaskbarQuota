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

        public static void Initialize(DispatcherQueue dispatcher, Action showMainWindow)
        {
            _dispatcher = dispatcher;
            _showMainWindow = showMainWindow;

            CreateTrayIcon();
            ShowWidget();

            UsageCoordinator.Instance.StateChanged += OnStateChanged;
            UsageCoordinator.Instance.ActiveProviderChanged += OnActiveProviderChanged;
            UsageCoordinator.Instance.ActiveToolPresenceChanged += OnActiveToolPresenceChanged;
            OnActiveToolPresenceChanged(UsageCoordinator.Instance.IsActiveToolPresent);
            App.Quitting += OnQuitting;
        }

        private static void CreateTrayIcon()
        {
            var open = new PopupMenuItem("Open TaskbarQuota", (_, _) => _dispatcher?.TryEnqueue(() => _showMainWindow?.Invoke()));
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
                if (e.MouseEvent is MouseEvent.IconLeftMouseUp or MouseEvent.IconLeftDoubleClick)
                    _dispatcher?.TryEnqueue(() => _showMainWindow?.Invoke());
            };
        }

        private static void ShowWidget()
        {
            if (_widget != null) return;
            try
            {
                var widget = new TaskBarWidget();
                widget.Initialize();
                widget.Destroying += (_, _) => _widget = null;
                if (widget.Summary is { } summary)
                    summary.Clicked += () => _dispatcher?.TryEnqueue(ToggleFlyout);
                widget.Show();
                _widget = widget;
                SyncWidgetState();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create taskbar widget");
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
                var target = coordinator.ActiveProvider ?? ProviderId.Codex;
                UsageResult? toApply = coordinator.LastState is { } last && last.Id == target
                    ? last
                    : coordinator.Service.TryGetCached(target, out var cached)
                        ? cached
                        : coordinator.LastState;

                if (toApply is { } result)
                    summary.Apply(result, force: true);

                summary.SetActiveToolVisible(coordinator.IsActiveToolPresent);

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

        private static void OnStateChanged(UsageResult result)
        {
            var widget = _widget;
            if (widget?.Summary is null) return;

            var active = UsageCoordinator.Instance.ActiveProvider ?? ProviderId.Codex;
            if (result.Id != active)
                return;

            widget.Summary.DispatcherQueue.TryEnqueue(() => widget.Summary.Apply(result));
        }

        private static void OnActiveProviderChanged(ProviderId? _) => SyncWidgetState();

        private static void OnActiveToolPresenceChanged(bool isPresent)
        {
            var widget = _widget;
            if (widget?.Summary is null) return;
            widget.Summary.DispatcherQueue.TryEnqueue(() => widget.Summary.SetActiveToolVisible(isPresent));
        }

        private static void OnQuitting()
        {
            UsageCoordinator.Instance.StateChanged -= OnStateChanged;
            UsageCoordinator.Instance.ActiveProviderChanged -= OnActiveProviderChanged;
            UsageCoordinator.Instance.ActiveToolPresenceChanged -= OnActiveToolPresenceChanged;
            if (_trayIcon != null) { _trayIcon.TryRemove(); _trayIcon.Dispose(); _trayIcon = null; }
            try { _flyout?.Close(); } catch { }
            _flyout = null;
            _widget?.Dispose();
            _widget = null;
        }
    }
}
