using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.System;
using TaskbarQuota.Controls;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Interop;

namespace TaskbarQuota.Taskbar
{
    /// <summary>
    /// Hosts a XAML island inside the Windows taskbar by SetParent-ing a layered popup window into
    /// Shell_TrayWnd. Auto-positions next to the system tray. Adapted from Awqat-Salaat's TaskBarWidget.
    /// </summary>
    internal sealed class TaskBarWidget : IDisposable
    {
        private const string TaskBarClassName = "Shell_TrayWnd";
        private const string ReBarWindow32ClassName = "ReBarWindow32";
        private const string NotificationAreaClassName = "TrayNotifyWnd";
        private const string WidgetsButtonAutomationId = "WidgetsButton";
        private const int DefaultWidgetHostWidth = 172;

        private static readonly bool IsRtlUI = System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

        private readonly double dpiScale;
        private readonly IntPtr hwndShell;
        private readonly IntPtr hwndTrayNotify;
        private readonly IntPtr hwndReBar;
        private readonly IntPtr hwndStart;
        private readonly string WidgetClassName = "TaskbarQuotaWidgetWinRT";
        private readonly TaskbarStructureWatcher taskbarWatcher;
        private readonly string positionPath;

        private IntPtr hwnd;
        private AppWindow? appWindow;
        private WidgetSummary? widgetSummary;
        private DesktopWindowXamlSource? host;
        private Microsoft.UI.Xaml.FrameworkElement? hostContent;
        private WndProc? wndProc;
        private int WidgetHostWidth;
        private int currentOffsetX = int.MinValue;
        private int currentOffsetY = 0;
        private bool isDragging;
        private bool isPointerTracking;
        private bool isDirectDrag;
        private int draggingInnerOffsetX;
        private int lastCursorPositionX;
        private int pressCursorPositionX;
        private bool initialized;
        private bool destroyed;
        private bool disposedValue;

        public IntPtr Handle => hwnd != IntPtr.Zero ? hwnd : throw new InvalidOperationException("Widget not initialized.");
        public WidgetSummary? Summary => widgetSummary;
        public event EventHandler? Destroying;

        public TaskBarWidget()
        {
            hwndShell = User32.FindWindow(TaskBarClassName, null);
            hwndTrayNotify = User32.FindWindowEx(hwndShell, IntPtr.Zero, NotificationAreaClassName, null);
            hwndReBar = User32.FindWindowEx(hwndShell, IntPtr.Zero, ReBarWindow32ClassName, null);
            hwndStart = User32.FindWindowEx(hwndShell, IntPtr.Zero, "Start", null);

            var dpi = User32.GetDpiForWindow(hwndShell);
            dpiScale = dpi / 96d;
            WidgetHostWidth = (int)Math.Ceiling(dpiScale * DefaultWidgetHostWidth);
            Log.Debug($"Widget ctor: DPI={dpi}, Width={WidgetHostWidth}");
            positionPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TaskbarQuota",
                "taskbar-widget-position.txt");

            taskbarWatcher = new TaskbarStructureWatcher(hwndShell, hwndReBar);
            taskbarWatcher.TaskbarChangedNotificationCompleted += (_, e) =>
            {
                if (initialized) _ = UpdatePositionImpl(e.Reason, e.IsTaskbarCentered, e.IsTaskbarWidgetsEnabled);
            };
        }

        public void Initialize()
        {
            Log.Information("Initializing widget host");
            host = new DesktopWindowXamlSource();
            hwnd = CreateHostWindow(hwndShell);

            var id = Win32Interop.GetWindowIdFromWindow(hwnd);
            appWindow = AppWindow.GetFromWindowId(id);
            appWindow.IsShownInSwitchers = false;
            appWindow.Destroying += AppWindow_Destroying;

            var taskbarRect = SystemInfos.GetTaskBarBounds();
            appWindow.ResizeClient(new SizeInt32(WidgetHostWidth, taskbarRect.bottom - taskbarRect.top));

            host.Initialize(id);
            host.SiteBridge.ResizePolicy = Microsoft.UI.Content.ContentSizePolicy.ResizeContentToParentWindow;
            widgetSummary = new WidgetSummary
            {
                Margin = new Microsoft.UI.Xaml.Thickness(4, 0, 4, 0),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch,
            };
            widgetSummary.DesiredHostWidthChanged += WidgetSummary_DesiredHostWidthChanged;
            widgetSummary.PointerPressed += WidgetSummary_PointerPressed;
            widgetSummary.PointerMoved += WidgetSummary_PointerMoved;
            widgetSummary.PointerReleased += WidgetSummary_PointerReleased;
            widgetSummary.PointerCanceled += WidgetSummary_PointerCanceled;
            hostContent = new Microsoft.UI.Xaml.Controls.Grid
            {
                Children = { widgetSummary },
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent)
            };
            host.Content = hostContent;
            ResizeWidgetHost(WidgetWidthForMode(WidgetSettingsService.Current));

            InjectIntoTaskbar();
            _ = UpdatePositionImpl(TaskbarChangeReason.None);

            initialized = true;
            Log.Information("Widget host initialization done");
        }

        private void InjectIntoTaskbar()
        {
            Log.Information("Injecting widget into taskbar");
            int attempts = 0;
            while (attempts++ <= 3)
            {
                var result = User32.SetParent(hwnd, hwndShell);
                if (result != IntPtr.Zero)
                {
                    Log.Information("Widget injected successfully");
                    return;
                }
            }
            Dispose();
            throw new InvalidOperationException("Could not inject the widget into the taskbar.");
        }

        private void AppWindow_Destroying(AppWindow sender, object args)
        {
            appWindow!.Destroying -= AppWindow_Destroying;
            Destroying?.Invoke(this, EventArgs.Empty);
            destroyed = true;
        }

        public void Show() => appWindow?.Show(false);
        public void Destroy() => appWindow?.Destroy();

        public void UpdatePosition(bool resetManualPosition = false)
        {
            if (resetManualPosition)
                SaveCustomPosition(-1);
            Task.Run(() => UpdatePositionImpl(TaskbarChangeReason.None));
        }

        private void WidgetSummary_DesiredHostWidthChanged(int logicalWidth)
        {
            if (ResizeWidgetHost(logicalWidth))
                UpdatePosition();
        }

        private bool ResizeWidgetHost(int logicalWidth)
        {
            if (appWindow is null)
                return false;

            var width = (int)Math.Ceiling(dpiScale * logicalWidth);
            if (WidgetHostWidth == width)
                return false;

            WidgetHostWidth = width;
            appWindow.ResizeClient(new SizeInt32(WidgetHostWidth, appWindow.Size.Height));
            return true;
        }

        private static int WidgetWidthForMode(WidgetDisplayMode mode) => mode switch
        {
            WidgetDisplayMode.PercentagesOnly => 220,
            WidgetDisplayMode.BarsAndPercentages => 280,
            _ => DefaultWidgetHostWidth,
        };

        private Task UpdatePositionImpl(TaskbarChangeReason reason)
            => UpdatePositionImpl(reason, SystemInfos.IsTaskBarCentered(), SystemInfos.IsTaskBarWidgetsEnabled());

        private async Task UpdatePositionImpl(TaskbarChangeReason reason, bool isCentered, bool isWidgetsEnabled)
        {
            if (appWindow is null) return;
            User32.GetWindowRect(hwndShell, out RECT taskbarRect);

            int offsetX = LoadCustomPosition();
            User32.GetWindowRect(hwndTrayNotify, out RECT trayNotifyRect);
            if (offsetX == -1)
            {
                _ = isWidgetsEnabled;
                RECT? wbRect = await taskbarWatcher.GetWidgetsButtonRectAsync();

                if (isCentered)
                {
                    offsetX = IsRtlUI ? (wbRect?.left ?? taskbarRect.right) - WidgetHostWidth : (wbRect?.right ?? 0);
                }
                else
                {
                    if (IsRtlUI)
                        offsetX = (wbRect is { } r && (r.left - trayNotifyRect.right) < WidgetHostWidth) ? r.right : trayNotifyRect.right;
                    else
                        offsetX = (wbRect is { } r && (trayNotifyRect.left - r.right) < WidgetHostWidth) ? r.left - WidgetHostWidth : trayNotifyRect.left - WidgetHostWidth;
                }

                try
                {
                    foreach (var wnd in GetOtherInjectedWindows())
                    {
                        User32.GetWindowRect(wnd, out var injectedBounds);
                        if (injectedBounds.right < offsetX || injectedBounds.left > (offsetX + WidgetHostWidth)) continue;
                        offsetX = (isCentered == IsRtlUI) ? injectedBounds.left - WidgetHostWidth : injectedBounds.right;
                    }
                }
                catch (Exception ex) { Log.Warning(ex, "overlap scan failed"); }
            }

            User32.GetWindowRect(hwndReBar, out RECT barRect);
            var lanes = GetWidgetLanes(taskbarRect, trayNotifyRect, barRect);
            offsetX = NormalizeWidgetX(offsetX, lanes, preferRightLane: true);
            int offsetY = barRect.top - taskbarRect.top;

            if (currentOffsetY != offsetY)
            {
                appWindow.MoveAndResize(new RectInt32(offsetX, offsetY, WidgetHostWidth, barRect.bottom - barRect.top));
                currentOffsetX = offsetX; currentOffsetY = offsetY;
            }
            else if (currentOffsetX != offsetX)
            {
                appWindow.Move(new PointInt32(offsetX, offsetY));
                currentOffsetX = offsetX;
            }
        }

        public void StartDragging()
        {
            if (isDragging || appWindow is null || hostContent is null || widgetSummary is null) return;
            widgetSummary.IsHitTestVisible = false;
            User32.SetCursorPos(appWindow.Position.X + appWindow.Size.Width / 2, appWindow.Position.Y + appWindow.Size.Height / 2);
            hostContent.KeyUp += Content_KeyUp;
            hostContent.PointerPressed += Content_PointerPressed;
            hostContent.PointerReleased += Content_PointerReleased;
            isDragging = true;
            if (!host!.HasFocus && hwnd != User32.GetForegroundWindow())
                User32.SetForegroundWindow(hwnd);
        }

        public void EndDragging(bool revert)
        {
            if (!isDragging || appWindow is null || hostContent is null || widgetSummary is null) return;
            isDragging = false;
            hostContent.ReleasePointerCaptures();
            hostContent.KeyUp -= Content_KeyUp;
            hostContent.PointerMoved -= Content_PointerMoved;
            hostContent.PointerPressed -= Content_PointerPressed;
            hostContent.PointerReleased -= Content_PointerReleased;
            widgetSummary.IsHitTestVisible = true;
            if (revert)
            {
                appWindow.Move(new PointInt32(currentOffsetX, currentOffsetY));
                return;
            }
            currentOffsetX = appWindow.Position.X;
            SaveCustomPosition(currentOffsetX);
        }

        private void Content_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
                EndDragging(true);
        }

        private void Content_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (appWindow is null || hostContent is null) return;
            e.Handled = true;
            hostContent.PointerMoved += Content_PointerMoved;
            hostContent.CapturePointer(e.Pointer);
            User32.GetCursorPos(out var point);
            lastCursorPositionX = point.x;
            draggingInnerOffsetX = point.x - appWindow.Position.X;
        }

        private void Content_PointerReleased(object sender, PointerRoutedEventArgs e) => EndDragging(false);

        private void Content_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (appWindow is null || hostContent is null) return;
            User32.GetCursorPos(out var point);
            MoveWidgetWithCursor(point.x);
            hostContent.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        }

        private void WidgetSummary_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (appWindow is null || widgetSummary is null) return;
            isPointerTracking = true;
            isDirectDrag = false;
            widgetSummary.CapturePointer(e.Pointer);
            User32.GetCursorPos(out var point);
            pressCursorPositionX = point.x;
            lastCursorPositionX = point.x;
            draggingInnerOffsetX = point.x - appWindow.Position.X;
        }

        private void WidgetSummary_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!isPointerTracking || appWindow is null || widgetSummary is null) return;
            User32.GetCursorPos(out var point);
            if (!isDirectDrag)
            {
                if (Math.Abs(point.x - pressCursorPositionX) < Math.Ceiling(4 * dpiScale))
                    return;
                isDirectDrag = true;
                widgetSummary.SuppressNextClick = true;
                e.Handled = true;
            }

            MoveWidgetWithCursor(point.x);
            widgetSummary.SuppressNextClick = true;
        }

        private void WidgetSummary_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (widgetSummary is not null)
                widgetSummary.ReleasePointerCaptures();
            if (isDirectDrag && appWindow is not null)
            {
                currentOffsetX = appWindow.Position.X;
                SaveCustomPosition(currentOffsetX);
                if (widgetSummary is not null)
                    widgetSummary.SuppressNextClick = true;
                e.Handled = true;
            }
            isPointerTracking = false;
            isDirectDrag = false;
        }

        private void WidgetSummary_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            isPointerTracking = false;
            isDirectDrag = false;
            widgetSummary?.ReleasePointerCaptures();
        }

        private void MoveWidgetWithCursor(int cursorX)
        {
            if (appWindow is null) return;
            User32.GetWindowRect(hwndShell, out RECT taskbarRect);
            User32.GetWindowRect(hwndTrayNotify, out RECT trayNotifyRect);
            User32.GetWindowRect(hwndReBar, out RECT barRect);
            var lanes = GetWidgetLanes(taskbarRect, trayNotifyRect, barRect);
            int targetX = cursorX - draggingInnerOffsetX;
            int delta = cursorX - lastCursorPositionX;
            targetX = NormalizeDragX(targetX, delta, lanes);

            appWindow.Move(new PointInt32(targetX, currentOffsetY));
            lastCursorPositionX = cursorX;
        }

        private TaskbarLanes GetWidgetLanes(RECT taskbarRect, RECT trayNotifyRect, RECT barRect)
        {
            int forbiddenLeft = barRect.left;
            int forbiddenRight = barRect.right;
            if (hwndStart != IntPtr.Zero && User32.GetWindowRect(hwndStart, out RECT startRect) && startRect.right > startRect.left)
            {
                forbiddenLeft = Math.Min(forbiddenLeft, startRect.left);
                forbiddenRight = Math.Max(forbiddenRight, startRect.right);
            }
            foreach (var bounds in GetTaskbarItemBandWindows())
            {
                forbiddenLeft = Math.Min(forbiddenLeft, bounds.left);
                forbiddenRight = Math.Max(forbiddenRight, bounds.right);
            }

            int leftMin;
            int leftMax;
            int rightMin;
            int rightMax;
            if (IsRtlUI)
            {
                leftMin = trayNotifyRect.right;
                leftMax = forbiddenLeft - WidgetHostWidth;
                rightMin = forbiddenRight;
                rightMax = taskbarRect.right - WidgetHostWidth;
            }
            else
            {
                leftMin = Math.Max(0, taskbarRect.left);
                leftMax = forbiddenLeft - WidgetHostWidth;
                rightMin = Math.Max(forbiddenRight, trayNotifyRect.left - WidgetHostWidth);
                rightMax = trayNotifyRect.left - WidgetHostWidth;
            }

            leftMax = Math.Max(leftMin, leftMax);
            rightMax = Math.Max(rightMin, rightMax);
            return new TaskbarLanes(leftMin, leftMax, rightMin, rightMax);
        }

        private int NormalizeWidgetX(int x, TaskbarLanes lanes, bool preferRightLane)
        {
            if (x <= lanes.LeftMax)
                return Math.Clamp(x, lanes.LeftMin, lanes.LeftMax);
            if (x >= lanes.RightMin)
                return Math.Clamp(x, lanes.RightMin, lanes.RightMax);
            return preferRightLane ? lanes.RightMin : lanes.LeftMax;
        }

        private int NormalizeDragX(int targetX, int cursorDelta, TaskbarLanes lanes)
        {
            if (targetX <= lanes.LeftMax)
                return Math.Clamp(targetX, lanes.LeftMin, lanes.LeftMax);
            if (targetX >= lanes.RightMin)
                return Math.Clamp(targetX, lanes.RightMin, lanes.RightMax);

            int currentX = appWindow?.Position.X ?? targetX;
            if (currentX >= lanes.RightMin)
                return lanes.RightMin;
            if (currentX <= lanes.LeftMax)
                return lanes.LeftMax;

            return cursorDelta < 0 ? lanes.RightMin : lanes.LeftMax;
        }

        private List<RECT> GetTaskbarItemBandWindows()
        {
            var windows = new List<RECT>();
            var gc = GCHandle.Alloc(windows);
            try { User32.EnumChildWindows(hwndShell, EnumTaskbarItemBandWindow, GCHandle.ToIntPtr(gc)); }
            finally { gc.Free(); }
            return windows;
        }

        private bool EnumTaskbarItemBandWindow(IntPtr hWnd, IntPtr lParam)
        {
            var builder = new StringBuilder(256);
            User32.GetClassName(hWnd, builder, builder.Capacity);
            string className = builder.ToString();
            if (className is not ("Start" or "TrayDummySearchControl" or "ReBarWindow32" or "MSTaskSwWClass" or "MSTaskListWClass"))
                return true;

            if (!User32.GetWindowRect(hWnd, out RECT bounds))
                return true;
            int width = bounds.right - bounds.left;
            int height = bounds.bottom - bounds.top;
            if (width <= 0 || height <= 0)
                return true;

            var list = GCHandle.FromIntPtr(lParam).Target as List<RECT>;
            list?.Add(bounds);
            return true;
        }

        private int LoadCustomPosition()
        {
            try
            {
                if (!File.Exists(positionPath))
                    return -1;
                return int.TryParse(File.ReadAllText(positionPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset)
                    ? offset
                    : -1;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read widget position");
                return -1;
            }
        }

        private void SaveCustomPosition(int offset)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(positionPath)!);
                File.WriteAllText(positionPath, offset.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not save widget position");
            }
        }

        private readonly record struct TaskbarLanes(int LeftMin, int LeftMax, int RightMin, int RightMax);

        private IntPtr CreateHostWindow(IntPtr parent)
        {
            RegisterWindowClass();
            return User32.CreateWindowEx(
                WindowStylesExtended.WS_EX_LAYERED, WidgetClassName, "WidgetHost",
                WindowStyles.WS_POPUP, 0, 0, 0, 0, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        private void RegisterWindowClass()
        {
            wndProc = WindowProc;
            var wndClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                hInstance = Kernel32.GetModuleHandle(null),
                lpfnWndProc = wndProc,
                lpszClassName = WidgetClassName,
            };
            User32.RegisterClassEx(ref wndClass);
        }

        private IntPtr WindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
            => User32.DefWindowProc(hWnd, uMsg, wParam, lParam);

        private List<IntPtr> GetOtherInjectedWindows()
        {
            var childHandles = new List<IntPtr>();
            var gc = GCHandle.Alloc(childHandles);
            try { User32.EnumChildWindows(hwndShell, EnumWindow, GCHandle.ToIntPtr(gc)); }
            finally { gc.Free(); }
            return childHandles;
        }

        private bool EnumWindow(IntPtr hWnd, IntPtr lParam)
        {
            if (hWnd != hwnd && User32.GetAncestor(hWnd, GetAncestorFlags.GA_PARENT) == hwndShell)
            {
                var builder = new StringBuilder(256);
                User32.GetClassName(hWnd, builder, builder.Capacity);
                var className = builder.ToString();
                if (!IsSystemWindow(className) && className != "#32770")
                {
                    var list = GCHandle.FromIntPtr(lParam).Target as List<IntPtr>;
                    list?.Add(hWnd);
                }
            }
            return true;

            static bool IsSystemWindow(string c) => c is "Start" or "TrayDummySearchControl" or "ReBarWindow32"
                or "TrayNotifyWnd" or "TrayButton" or "DynamicContent2"
                or "Windows.UI.Core.CoreWindow" or "Windows.UI.Composition.DesktopWindowContentBridge";
        }

        public void Dispose()
        {
            if (disposedValue) return;
            if (!destroyed) appWindow?.Destroy();
            if (widgetSummary is not null)
            {
                widgetSummary.PointerPressed -= WidgetSummary_PointerPressed;
                widgetSummary.DesiredHostWidthChanged -= WidgetSummary_DesiredHostWidthChanged;
                widgetSummary.PointerMoved -= WidgetSummary_PointerMoved;
                widgetSummary.PointerReleased -= WidgetSummary_PointerReleased;
                widgetSummary.PointerCanceled -= WidgetSummary_PointerCanceled;
            }
            taskbarWatcher.Dispose();
            host?.Dispose();
            User32.UnregisterClass(WidgetClassName, Kernel32.GetModuleHandle(null));
            disposedValue = true;
            GC.SuppressFinalize(this);
        }
    }
}
