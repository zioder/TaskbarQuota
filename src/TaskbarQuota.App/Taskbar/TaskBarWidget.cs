using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
    /// a primary or secondary taskbar. Auto-positions next to the system tray on the primary taskbar and
    /// at the free outer edge on secondary taskbars. Adapted from Awqat-Salaat's TaskBarWidget.
    /// </summary>
    internal sealed class TaskBarWidget : IDisposable
    {
        private const string ReBarWindow32ClassName = "ReBarWindow32";
        private const string NotificationAreaClassName = "TrayNotifyWnd";
        private const string WidgetsButtonAutomationId = "WidgetsButton";
        private const int DefaultWidgetHostWidth = 172;
        private const int TrayClearanceLogicalPx = 6;
        private static readonly TimeSpan PositionDisposeWait = TimeSpan.FromSeconds(3);
        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        // Approx width of the Win11 far-left Widgets/weather pill; used to reserve clearance when its exact
        // bounds can't be read via UIA, so the widget never anchors on top of it (issue #17).
        private const int WidgetsButtonFallbackLogicalPx = 160;

        private static readonly bool IsRtlUI = System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
        private static readonly object WindowClassLock = new();
        private static readonly WndProc SharedWndProc = SharedWindowProc;
        private static bool windowClassRegistered;
        private static int windowClassUsers;

        // Minimum horizontal recompute delta (logical px, DPI-scaled) before the resting widget is moved.
        private int RepositionDeadbandPx => (int)Math.Ceiling(2 * dpiScale);
        private int TrayClearancePx => (int)Math.Ceiling(TrayClearanceLogicalPx * dpiScale);

        private readonly double dpiScale;
        private readonly uint taskbarDpi;
        private readonly IntPtr hwndShell;
        private readonly IntPtr hwndTrayNotify;
        private readonly IntPtr hwndReBar;
        private readonly IntPtr hwndStart;
        private readonly bool isPrimaryTaskbar;
        private readonly string displayKey;
        private readonly string WidgetClassName = "TaskbarQuotaWidgetWinRT";
        private readonly TaskbarStructureWatcher taskbarWatcher;
        private readonly string positionPath;
        private readonly CancellationTokenSource positionUpdateCancellation = new();
        private readonly SemaphoreSlim positionUpdateGate = new(1, 1);
        private readonly object positionRequestLock = new();

        private IntPtr hwnd;
        private AppWindow? appWindow;
        private WidgetSummary? widgetSummary;
        private DesktopWindowXamlSource? host;
        private Microsoft.UI.Xaml.FrameworkElement? hostContent;
        private int WidgetHostWidth;
        private int currentOffsetX = int.MinValue;
        private int currentOffsetY = 0;
        // Last known Widgets/weather pill bounds in taskbar-client coords, captured during the resting
        // reposition so the synchronous drag path can avoid it without an async UIA read.
        private RECT? lastWidgetsButtonClientRect;
        // Last known taskbar button bounds (app icons + system buttons) in taskbar-client coords. On Win11
        // the app icons are XAML, not classic child windows, so they only come from a UIA scan; cached here
        // so the synchronous drag path avoids them without an async read (issue #17).
        private List<RECT> lastTaskButtonClientRects = new();
        private bool isDragging;
        private bool isPointerTracking;
        private bool isDirectDrag;

        // True while the user is actively repositioning the widget (tray "Move" mode or a direct pointer
        // drag). Background repositions (2s watcher poll, taskbar events) must not fire during this, or the
        // widget snaps back to a computed lane mid-drag (issue #17 case 2).
        // isSettling covers the async snap right after release: without it a watcher poll landing mid-snap
        // would recompute a resting position from the not-yet-saved offset and yank the widget away.
        private bool IsUserRepositioning => isDragging || isPointerTracking || isDirectDrag || isSettling;
        private bool isSettling;
        private int draggingInnerOffsetX;
        // Where the drag currently sits, and the free gap it is tracking the cursor inside.
        private int? dragPreviewX;
        private (int start, int end)? activeDragGap;
        private int lastCursorPositionX;
        private int pressCursorPositionX;
        private bool initialized;
        private bool destroyed;
        private bool isVisible;
        private bool windowClassAcquired;
        private bool disposedValue;
        private bool positionRunnerActive;
        private bool positionUpdatePending;
        private TaskbarChangeReason pendingPositionReason;
        private bool pendingTaskbarCentered;
        private bool pendingTaskbarWidgetsEnabled;

        public IntPtr Handle => hwnd != IntPtr.Zero ? hwnd : throw new InvalidOperationException("Widget not initialized.");
        public bool IsAlive => hwnd != IntPtr.Zero && User32.IsWindow(hwnd);
        public bool IsDpiCurrent
        {
            get
            {
                if (!User32.IsWindow(hwndShell))
                    return false;
                uint currentDpi = User32.GetDpiForWindow(hwndShell);
                return (currentDpi == 0 ? 96u : currentDpi) == taskbarDpi;
            }
        }
        public IntPtr TaskbarHandle => hwndShell;
        public bool IsPrimaryTaskbar => isPrimaryTaskbar;
        public WidgetSummary? Summary => widgetSummary;
        public event EventHandler? Destroying;

        public TaskBarWidget(TaskbarWindowTarget target)
        {
            hwndShell = target.Handle;
            isPrimaryTaskbar = target.IsPrimary;
            displayKey = target.DisplayKey;
            hwndTrayNotify = User32.FindWindowEx(hwndShell, IntPtr.Zero, NotificationAreaClassName, null);
            hwndReBar = User32.FindWindowEx(hwndShell, IntPtr.Zero, ReBarWindow32ClassName, null);
            hwndStart = User32.FindWindowEx(hwndShell, IntPtr.Zero, "Start", null);

            if (hwndShell == IntPtr.Zero || !User32.IsWindow(hwndShell)
                || (isPrimaryTaskbar && (hwndTrayNotify == IntPtr.Zero || hwndReBar == IntPtr.Zero)))
                throw new InvalidOperationException("Windows taskbar is not ready.");

            uint detectedDpi = User32.GetDpiForWindow(hwndShell);
            taskbarDpi = detectedDpi == 0 ? 96u : detectedDpi;
            dpiScale = taskbarDpi / 96d;
            WidgetHostWidth = (int)Math.Ceiling(dpiScale * DefaultWidgetHostWidth);
            Log.Debug($"Widget ctor: taskbar=0x{hwndShell.ToInt64():X}, primary={isPrimaryTaskbar}, DPI={taskbarDpi}, Width={WidgetHostWidth}");
            positionPath = target.GetPositionPath();

            taskbarWatcher = new TaskbarStructureWatcher(hwndShell, hwndReBar);
            taskbarWatcher.TaskbarChangedNotificationCompleted += (_, e) =>
            {
                if (initialized)
                    QueuePositionUpdate(e.Reason, e.IsTaskbarCentered, e.IsTaskbarWidgetsEnabled);
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

            if (!User32.GetWindowRect(hwndShell, out var taskbarRect)
                || taskbarRect.right <= taskbarRect.left
                || taskbarRect.bottom <= taskbarRect.top)
            {
                throw new InvalidOperationException("Windows taskbar bounds are not ready.");
            }
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
            QueuePositionUpdate(TaskbarChangeReason.None);

            initialized = true;
            Log.Information("Widget host initialization done");
        }

        private void InjectIntoTaskbar()
        {
            Log.Information("Injecting widget into taskbar");
            int attempts = 0;
            while (attempts++ <= 3)
            {
                var previousParent = User32.SetParent(hwnd, hwndShell);
                if (previousParent != IntPtr.Zero || IsParentedToTaskbar())
                {
                    Log.Information("Widget injected successfully");
                    return;
                }
            }
            Dispose();
            throw new InvalidOperationException("Could not inject the widget into the taskbar.");
        }

        private bool IsParentedToTaskbar()
            => User32.GetAncestor(hwnd, GetAncestorFlags.GA_PARENT) == hwndShell;

        private void AppWindow_Destroying(AppWindow sender, object args)
        {
            appWindow!.Destroying -= AppWindow_Destroying;
            destroyed = true;
            Destroying?.Invoke(this, EventArgs.Empty);
        }

        public bool MatchesTarget(TaskbarWindowTarget target)
            => target.Handle == hwndShell
                && target.IsPrimary == isPrimaryTaskbar
                && string.Equals(target.DisplayKey, displayKey, StringComparison.Ordinal);

        public void SetVisible(bool visible)
        {
            if (appWindow is null || isVisible == visible)
                return;

            isVisible = visible;
            if (visible)
            {
                QueuePositionUpdate(TaskbarChangeReason.None);
                appWindow.Show(false);
            }
            else
            {
                if (isDragging)
                    EndDragging(revert: true);
                appWindow.Hide();
            }
        }

        public void Destroy() => appWindow?.Destroy();

        public void UpdatePosition(bool resetManualPosition = false)
        {
            if (resetManualPosition)
                SaveCustomPosition(-1);
            QueuePositionUpdate(TaskbarChangeReason.None);
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

        private void QueuePositionUpdate(TaskbarChangeReason reason)
            => QueuePositionUpdate(reason, SystemInfos.IsTaskBarCentered(), SystemInfos.IsTaskBarWidgetsEnabled());

        private void QueuePositionUpdate(TaskbarChangeReason reason, bool isCentered, bool isWidgetsEnabled)
        {
            lock (positionRequestLock)
            {
                if (disposedValue)
                    return;

                // Coalesce any number of watcher/layout requests into the latest pending state. Alignment
                // invalidates a saved manual position, so preserve that reason until the pending pass runs.
                if (!positionUpdatePending || reason == TaskbarChangeReason.Alignment)
                    pendingPositionReason = reason;
                pendingTaskbarCentered = isCentered;
                pendingTaskbarWidgetsEnabled = isWidgetsEnabled;
                positionUpdatePending = true;

                if (positionRunnerActive)
                    return;

                positionRunnerActive = true;
            }

            _ = ProcessPositionUpdatesAsync();
        }

        private async Task ProcessPositionUpdatesAsync()
        {
            while (true)
            {
                TaskbarChangeReason reason;
                bool isCentered;
                bool isWidgetsEnabled;
                lock (positionRequestLock)
                {
                    if (disposedValue || !positionUpdatePending)
                    {
                        positionRunnerActive = false;
                        return;
                    }

                    reason = pendingPositionReason;
                    isCentered = pendingTaskbarCentered;
                    isWidgetsEnabled = pendingTaskbarWidgetsEnabled;
                    positionUpdatePending = false;
                }

                await UpdatePositionImpl(reason, isCentered, isWidgetsEnabled);
            }
        }

        private async Task UpdatePositionImpl(TaskbarChangeReason reason, bool isCentered, bool isWidgetsEnabled)
        {
            bool gateAcquired = false;
            var cancellationToken = positionUpdateCancellation.Token;
            try
            {
                await positionUpdateGate.WaitAsync(cancellationToken);
                gateAcquired = true;
                cancellationToken.ThrowIfCancellationRequested();

                if (disposedValue || appWindow is null || IsUserRepositioning)
                    return;
                if (!TryGetLayoutRects(
                        out RECT taskbarScreenRect,
                        out RECT notificationScreenRect,
                        out RECT barScreenRect,
                        out bool hasNotificationArea))
                {
                    return;
                }

                var taskbarRect = ToTaskbarClientRect(taskbarScreenRect, taskbarScreenRect);
                var trayNotifyRect = ToTaskbarClientRect(notificationScreenRect, taskbarScreenRect);
                var barRect = ToTaskbarClientRect(barScreenRect, taskbarScreenRect);

                int offsetX = LoadCustomPosition();
                // Taskbar alignment flipped (e.g. centered -> left): the old manual position now sits on the
                // wrong side, so discard it and re-anchor to the new side's default lane (issue #10).
                if (reason == TaskbarChangeReason.Alignment && offsetX != -1)
                {
                    SaveCustomPosition(-1);
                    offsetX = -1;
                }
                bool useDefault = offsetX == -1;

                // The Widgets/weather pill is a XAML element the child-window scan can't see, so fetch its
                // bounds separately (UIA, with a cached fallback) and treat it as an obstacle like any other.
                RECT? wbRect = isWidgetsEnabled ? await taskbarWatcher.GetWidgetsButtonRectAsync() : null;
                cancellationToken.ThrowIfCancellationRequested();
                RECT? wbClient = wbRect is { } wb && wb.right > wb.left ? ToTaskbarClientRect(wb, taskbarScreenRect) : null;
                lastWidgetsButtonClientRect = wbClient;

                // UIA scan of the taskbar's buttons — the only way to see the Win11 XAML app icons. Cache the
                // client rects so the synchronous drag path can reuse them without an async read.
                var taskButtonRects = await taskbarWatcher.GetTaskbarButtonRectsAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (taskButtonRects is not null)
                {
                    var converted = new List<RECT>(taskButtonRects.Count);
                    foreach (var r in taskButtonRects)
                        converted.Add(ToTaskbarClientRect(r, taskbarScreenRect));
                    lastTaskButtonClientRects = converted;
                }

                // Every taskbar button (app icons + system buttons) is an obstacle, so the widget can never rest
                // on top of the app cluster — same set for resting and dragging (issue #17).
                var obstacles = CollectObstacleClientRects(taskbarScreenRect, wbClient, lastTaskButtonClientRects);

                var (leftBound, rightBound) = ComputeUsableHorizontalBounds(
                    taskbarRect,
                    hasNotificationArea ? trayNotifyRect : null,
                    TrayClearancePx,
                    IsRtlUI);

                // Preferred X: the saved manual position, or the side-appropriate default anchor.
                int preferredX;
                if (!useDefault)
                    preferredX = offsetX;
                else if (!hasNotificationArea)
                    preferredX = IsRtlUI ? leftBound : rightBound - WidgetHostWidth;
                else if (isCentered)
                    preferredX = ComputeFarLeftAnchor(taskbarRect, trayNotifyRect, wbRect, taskbarScreenRect, isCentered, isWidgetsEnabled);
                else
                    preferredX = IsRtlUI ? leftBound : rightBound - WidgetHostWidth;

                // Only ever rest inside a gap that FULLY fits the widget; if none does, don't move it into an
                // overlap — keep the last valid spot (issue #17). First run with no fit hugs the tray.
                var gaps = ComputeFreeGaps(leftBound, rightBound, obstacles);
                int? placed = PlaceInFittingGap(preferredX, gaps, WidgetHostWidth);
                if (placed is not { } fitX)
                {
                    if (currentOffsetX != int.MinValue)
                        return;
                    offsetX = Math.Max(leftBound, rightBound - WidgetHostWidth);
                }
                else
                {
                    offsetX = fitX;
                }

                offsetX = ClampToTaskbarMonitor(
                    offsetX,
                    WidgetHostWidth,
                    taskbarScreenRect,
                    notificationScreenRect,
                    barScreenRect,
                    hwndShell,
                    hasNotificationArea);

                int offsetY = barRect.top;
                cancellationToken.ThrowIfCancellationRequested();
                var targetAppWindow = appWindow;
                if (disposedValue || targetAppWindow is null || !IsAlive)
                    return;

                if (currentOffsetY != offsetY)
                {
                    targetAppWindow.MoveAndResize(new RectInt32(offsetX, offsetY, WidgetHostWidth, barRect.bottom - barRect.top));
                    currentOffsetX = offsetX; currentOffsetY = offsetY;
                }
                else if (Math.Abs(currentOffsetX - offsetX) >= RepositionDeadbandPx)
                {
                    // Deadband: ignore sub-threshold recompute deltas (rounding / transient tray width changes)
                    // so the widget doesn't visibly twitch on routine taskbar events.
                    targetAppWindow.Move(new PointInt32(offsetX, offsetY));
                    currentOffsetX = offsetX;
                }
            }
            catch (OperationCanceledException)
            {
                // Widget disposal/topology reconciliation cancels pending UIA placement work.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Taskbar widget position update failed for taskbar=0x{hwndShell.ToInt64():X}");
            }
            finally
            {
                if (gateAcquired)
                    positionUpdateGate.Release();
            }
        }

        public void StartDragging()
        {
            if (isDragging || appWindow is null || hostContent is null || widgetSummary is null) return;
            SetVisible(true);
            widgetSummary.IsHitTestVisible = false;
            User32.GetWindowRect(hwndShell, out var taskbarRect);
            User32.SetCursorPos(
                taskbarRect.left + appWindow.Position.X + appWindow.Size.Width / 2,
                taskbarRect.top + appWindow.Position.Y + appWindow.Size.Height / 2);
            hostContent.KeyUp += Content_KeyUp;
            hostContent.PointerPressed += Content_PointerPressed;
            hostContent.PointerReleased += Content_PointerReleased;
            isDragging = true;
            PrimeObstacleCacheForDrag();
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
                dragPreviewX = null;
                activeDragGap = null;
                appWindow.Move(new PointInt32(currentOffsetX, currentOffsetY));
                return;
            }
            _ = SnapToValidPositionAsync(dragPreviewX ?? appWindow.Position.X);
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
            User32.GetWindowRect(hwndShell, out var taskbarRect);
            draggingInnerOffsetX = point.x - taskbarRect.left - appWindow.Position.X;
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
            PrimeObstacleCacheForDrag();
            widgetSummary.CapturePointer(e.Pointer);
            User32.GetCursorPos(out var point);
            pressCursorPositionX = point.x;
            lastCursorPositionX = point.x;
            User32.GetWindowRect(hwndShell, out var taskbarRect);
            draggingInnerOffsetX = point.x - taskbarRect.left - appWindow.Position.X;
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
                _ = SnapToValidPositionAsync(dragPreviewX ?? appWindow.Position.X);
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

        /// <summary>
        /// Drags the widget with the cursor inside whichever free gap the CURSOR currently occupies, so it
        /// tracks the pointer smoothly across a whole zone and never overlaps a shell element.
        ///
        /// Selecting the gap by cursor position (rather than by distance to the widget, as before) is what
        /// fixes issue #21: while the cursor crosses the centred icon cluster the widget simply waits,
        /// pinned to the edge of the gap it is in, and picks up the pointer again the moment the cursor
        /// enters the next gap. The old "nearest fitting gap" rule flipped between the left and right zones
        /// mid-drag, which read as the widget jumping between lanes at random or refusing to move.
        /// </summary>
        private void MoveWidgetWithCursor(int cursorX)
        {
            if (appWindow is null) return;
            if (!TryGetLayoutRects(
                    out RECT taskbarRect,
                    out RECT notificationRect,
                    out _,
                    out bool hasNotificationArea))
            {
                return;
            }
            var taskbarClientRect = ToTaskbarClientRect(taskbarRect, taskbarRect);
            var trayNotifyClientRect = ToTaskbarClientRect(notificationRect, taskbarRect);

            var (leftBound, rightBound) = ComputeUsableHorizontalBounds(
                taskbarClientRect,
                hasNotificationArea ? trayNotifyClientRect : null,
                TrayClearancePx,
                IsRtlUI);
            var obstacles = CollectObstacleClientRects(taskbarRect, lastWidgetsButtonClientRect, lastTaskButtonClientRects);
            var gaps = ComputeFreeGaps(leftBound, rightBound, obstacles);

            int cursorClientX = cursorX - taskbarRect.left;
            int desiredX = cursorClientX - draggingInnerOffsetX;

            var gap = SelectDragGap(gaps, cursorClientX, desiredX, WidgetHostWidth, activeDragGap);
            if (gap is not { } zone)
            {
                // No gap can hold the widget at all (very crowded bar): leave it where it is.
                lastCursorPositionX = cursorX;
                return;
            }

            activeDragGap = zone;
            int targetX = Math.Clamp(desiredX, zone.start, zone.end - WidgetHostWidth);

            appWindow.Move(new PointInt32(targetX, currentOffsetY));
            dragPreviewX = targetX;
            ResyncGrabPoint(cursorClientX, targetX, desiredX);
            lastCursorPositionX = cursorX;
        }

        /// <summary>
        /// Re-anchors the grab point whenever the widget is pinned and the cursor has run past it (span end
        /// or icon cluster). Without this the pointer builds up an invisible offset and the drag feels dead
        /// until the hand travels all the way back.
        ///
        /// Awqat-Salaat solves the same problem by clamping the physical cursor with SetCursorPos, but that
        /// traps the user's mouse — it cannot be moved past the taskbar end while a drag is held. Moving the
        /// grab point instead keeps the pointer completely free and still responds the instant the user
        /// reverses direction. The offset stays within the widget so the grab never leaves the control.
        /// </summary>
        private void ResyncGrabPoint(int cursorClientX, int targetX, int desiredX)
        {
            if (desiredX == targetX)
                return;

            draggingInnerOffsetX = Math.Clamp(cursorClientX - targetX, 0, WidgetHostWidth);
        }

        /// <summary>
        /// Chooses the gap the dragged widget lives in for this pointer sample:
        /// the gap under the cursor when it fits the widget; otherwise the gap the drag is already in
        /// (so passing over an icon cluster doesn't teleport the widget); otherwise the nearest fitting gap.
        /// Returns null when no gap can hold the widget.
        /// </summary>
        internal static (int start, int end)? SelectDragGap(
            List<(int start, int end)> gaps, int cursorX, int desiredX, int width, (int start, int end)? current)
        {
            (int start, int end)? underCursor = null;
            (int start, int end)? sticky = null;
            (int start, int end)? nearest = null;
            long nearestDistance = long.MaxValue;

            foreach (var gap in gaps)
            {
                if (gap.end - gap.start < width)
                    continue;

                if (cursorX >= gap.start && cursorX < gap.end)
                    underCursor = gap;

                // The current gap is matched by overlap, not equality: obstacle bounds shift by a pixel or
                // two between samples as the shell relayouts, which would otherwise drop the sticky gap.
                if (current is { } c && gap.start < c.end && gap.end > c.start)
                    sticky = gap;

                long distance = Math.Abs((long)Math.Clamp(desiredX, gap.start, gap.end - width) - desiredX);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = gap;
                }
            }

            return underCursor ?? sticky ?? nearest;
        }

        /// <summary>
        /// Settles the widget after a drag: snaps the dropped position to the nearest gap that fully fits
        /// it, so it rests beside shell elements instead of on top of them. Obstacle bounds are re-read
        /// here (UIA, off the UI thread) rather than during the drag, which keeps the drag itself smooth.
        /// Falls back to the pre-drag position when nothing fits.
        /// </summary>
        private async Task SnapToValidPositionAsync(int droppedX)
        {
            if (appWindow is null) return;

            isSettling = true;
            try
            {
                if (!TryGetLayoutRects(
                        out RECT taskbarScreenRect,
                        out RECT notificationScreenRect,
                        out _,
                        out bool hasNotificationArea))
                {
                    return;
                }
                var taskbarRect = ToTaskbarClientRect(taskbarScreenRect, taskbarScreenRect);
                var trayNotifyRect = ToTaskbarClientRect(notificationScreenRect, taskbarScreenRect);

                await RefreshObstacleCacheAsync(taskbarScreenRect);

                var obstacles = CollectObstacleClientRects(taskbarScreenRect, lastWidgetsButtonClientRect, lastTaskButtonClientRects);
                var (leftBound, rightBound) = ComputeUsableHorizontalBounds(
                    taskbarRect,
                    hasNotificationArea ? trayNotifyRect : null,
                    TrayClearancePx,
                    IsRtlUI);
                var gaps = ComputeFreeGaps(leftBound, rightBound, obstacles);

                int settledX = PlaceInFittingGap(droppedX, gaps, WidgetHostWidth)
                    ?? (currentOffsetX != int.MinValue ? currentOffsetX : ClampToSpan(droppedX, leftBound, rightBound, WidgetHostWidth));

                appWindow.Move(new PointInt32(settledX, currentOffsetY));
                currentOffsetX = settledX;
                dragPreviewX = null;
                activeDragGap = null;
                SaveCustomPosition(settledX);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to settle widget after drag");
            }
            finally
            {
                isSettling = false;
            }
        }

        /// <summary>
        /// Re-reads the obstacle bounds only visible through UI Automation (the Win11 XAML app icons and
        /// the Widgets/weather pill) into the caches the synchronous drag path reads. Called when a drag
        /// starts and when it ends, so the gaps the drag tracks reflect the bar as it is right now.
        /// </summary>
        private async Task RefreshObstacleCacheAsync(RECT taskbarScreenRect)
        {
            if (SystemInfos.IsTaskBarWidgetsEnabled()
                && await taskbarWatcher.GetWidgetsButtonRectAsync() is { } wb && wb.right > wb.left)
            {
                lastWidgetsButtonClientRect = ToTaskbarClientRect(wb, taskbarScreenRect);
            }

            if (await taskbarWatcher.GetTaskbarButtonRectsAsync() is { } taskButtonRects)
            {
                var converted = new List<RECT>(taskButtonRects.Count);
                foreach (var r in taskButtonRects)
                    converted.Add(ToTaskbarClientRect(r, taskbarScreenRect));
                lastTaskButtonClientRects = converted;
            }
        }

        private void PrimeObstacleCacheForDrag()
        {
            activeDragGap = null;
            User32.GetWindowRect(hwndShell, out RECT taskbarScreenRect);
            _ = RefreshObstacleCacheAsync(taskbarScreenRect);
        }


        /// <summary>Keeps a widget of <paramref name="width"/> inside [leftBound, rightBound].</summary>
        internal static int ClampToSpan(int desiredX, int leftBound, int rightBound, int width)
        {
            int maxX = rightBound - width;
            return maxX <= leftBound ? leftBound : Math.Clamp(desiredX, leftBound, maxX);
        }

        // Collects the taskbar-client rects of everything the widget must not overlap: the Start button and
        // search box (classic child windows), every taskbar button from the UIA scan (the Win11 XAML app
        // icons plus system buttons), the Widgets/weather pill, and any other injected sibling widgets. The
        // ReBarWindow32 container is excluded — it spans the whole item area and would leave no gaps.
        // taskButtonClientRects is the cached UIA set so both the resting and drag paths use identical
        // obstacles (issue #17).
        private List<RECT> CollectObstacleClientRects(RECT taskbarScreenRect, RECT? widgetsPillClient, List<RECT> taskButtonClientRects)
        {
            var result = new List<RECT>();

            if (hwndStart != IntPtr.Zero && User32.GetWindowRect(hwndStart, out RECT startRect) && startRect.right > startRect.left)
                result.Add(ToTaskbarClientRect(startRect, taskbarScreenRect));

            // Classic taskbars (Win10 / third-party shells) expose Start/search as child windows here; on
            // Win11 these return little and the UIA button set below carries the app icons.
            foreach (var bounds in GetTaskbarItemBandWindows(includeAppButtons: true, excludeContainer: true))
                result.Add(ToTaskbarClientRect(bounds, taskbarScreenRect));

            result.AddRange(taskButtonClientRects);

            if (widgetsPillClient is { } pill && pill.right > pill.left)
                result.Add(pill);

            try
            {
                foreach (var wnd in GetOtherInjectedWindows())
                {
                    if (User32.GetWindowRect(wnd, out var injectedBounds) && injectedBounds.right > injectedBounds.left)
                        result.Add(ToTaskbarClientRect(injectedBounds, taskbarScreenRect));
                }
            }
            catch (Exception ex) { Log.Warning(ex, "overlap scan failed"); }

            return FilterContainerRects(result, taskbarScreenRect.right - taskbarScreenRect.left);
        }

        /// <summary>
        /// Drops obstacle rects that are really containers, not elements. The UIA tree exposes grouping
        /// elements whose bounds span most of the bar; treating one as an obstacle wipes out every free gap,
        /// which is why the widget could get stuck in a narrow band or refuse to move at all (issue #21).
        /// </summary>
        internal static List<RECT> FilterContainerRects(List<RECT> rects, int taskbarWidth)
        {
            if (taskbarWidth <= 0)
                return rects;

            int maxObstacleWidth = taskbarWidth / 2;
            var kept = new List<RECT>(rects.Count);
            foreach (var r in rects)
            {
                if (r.right - r.left <= maxObstacleWidth)
                    kept.Add(r);
            }
            return kept;
        }

        // Merges the obstacle rects (clipped to [leftBound, rightBound]) and returns the free horizontal
        // gaps between them. Each gap is a [start, end) span the widget could occupy.
        internal static List<(int start, int end)> ComputeFreeGaps(int leftBound, int rightBound, List<RECT> obstacles)
        {
            var gaps = new List<(int, int)>();
            if (rightBound <= leftBound)
                return gaps;

            var blocked = new List<(int start, int end)>();
            foreach (var o in obstacles)
            {
                int s = Math.Max(leftBound, o.left);
                int e = Math.Min(rightBound, o.right);
                if (e > s)
                    blocked.Add((s, e));
            }
            blocked.Sort((a, b) => a.start.CompareTo(b.start));

            int cursor = leftBound;
            foreach (var (s, e) in blocked)
            {
                if (s > cursor)
                    gaps.Add((cursor, s));
                cursor = Math.Max(cursor, e);
            }
            if (cursor < rightBound)
                gaps.Add((cursor, rightBound));

            return gaps;
        }

        // Picks the position closest to preferredX that fully fits a widget of the given width inside one of
        // the free gaps. Returns null when no gap is wide enough — the caller then declines to move rather
        // than force an overlap (issue #17).
        internal static int? PlaceInFittingGap(int preferredX, List<(int start, int end)> gaps, int width)
        {
            int? best = null;
            long bestDist = long.MaxValue;
            foreach (var (start, end) in gaps)
            {
                if (end - start < width)
                    continue;
                int candidate = Math.Clamp(preferredX, start, end - width);
                long dist = Math.Abs((long)candidate - preferredX);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }
            return best;
        }

        internal static (int left, int right) ComputeUsableHorizontalBounds(
            RECT taskbarRect,
            RECT? notificationRect,
            int clearance,
            bool isRtl)
        {
            if (notificationRect is not { } tray)
            {
                int left = isRtl ? Math.Min(taskbarRect.right, Math.Max(taskbarRect.left, clearance)) : taskbarRect.left;
                int right = isRtl
                    ? taskbarRect.right
                    : Math.Max(left, taskbarRect.right - clearance);
                return (left, right);
            }

            int leftBound = isRtl ? Math.Max(taskbarRect.left, tray.right) : taskbarRect.left;
            int rightBound = Math.Max(
                leftBound,
                (isRtl ? taskbarRect.right : tray.left) - clearance);
            if (!isRtl)
                rightBound = Math.Min(rightBound, taskbarRect.right);
            return (leftBound, rightBound);
        }

        // The default "far left" X: hugging the left end of the taskbar's item area. On Win11 the Widgets
        // button is pinned far-left, so we sit just right of it; on a left-aligned classic taskbar we sit
        // right of the Start button; otherwise the very left edge. RTL mirrors to the visual start (right).
        private int ComputeFarLeftAnchor(RECT taskbarRect, RECT trayNotifyRect, RECT? wbRect, RECT taskbarScreenRect, bool isCentered, bool isWidgetsEnabled)
        {
            int fallbackPillWidth = (int)Math.Ceiling(WidgetsButtonFallbackLogicalPx * dpiScale);

            if (IsRtlUI)
            {
                if (wbRect is { } wb && wb.right > wb.left)
                    return ToTaskbarClientRect(wb, taskbarScreenRect).left - WidgetHostWidth;
                // Widgets pill sits at the visual start (right) but its bounds are unknown: reserve clearance.
                int rightAnchor = taskbarRect.right - WidgetHostWidth;
                if (isWidgetsEnabled)
                    rightAnchor -= fallbackPillWidth;
                return rightAnchor;
            }

            int anchor = Math.Max(0, taskbarRect.left);
            if (wbRect is { } w && w.right > w.left)
                anchor = Math.Max(anchor, ToTaskbarClientRect(w, taskbarScreenRect).right);
            else if (isWidgetsEnabled)
                // Widgets enabled but its exact bounds are unavailable (UIA not ready): step past a
                // conservative pill width so we don't anchor on top of the weather pill (issue #17).
                anchor = Math.Max(anchor, Math.Max(0, taskbarRect.left) + fallbackPillWidth);
            else if (!isCentered && hwndStart != IntPtr.Zero
                     && User32.GetWindowRect(hwndStart, out RECT startRect) && startRect.right > startRect.left)
                anchor = Math.Max(anchor, ToTaskbarClientRect(startRect, taskbarScreenRect).right);
            return anchor;
        }

        // On multi-monitor setups where one taskbar spans displays (e.g. Open-Shell), the widget can land
        // straddling the seam. Primary widgets follow the notification area's monitor; secondary widgets
        // follow the monitor that owns their taskbar window.
        private static int ClampToTaskbarMonitor(
            int offsetX,
            int widgetHostWidth,
            RECT taskbarRect,
            RECT trayNotifyRect,
            RECT barRect,
            IntPtr hwndTaskbar,
            bool hasNotificationArea)
        {
            IntPtr monitor;
            if (hasNotificationArea)
            {
                var anchor = new POINT { x = trayNotifyRect.left - 1, y = (barRect.top + barRect.bottom) / 2 };
                monitor = User32.MonitorFromPoint(anchor, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            }
            else
            {
                monitor = User32.MonitorFromWindow(hwndTaskbar, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            }

            if (monitor == IntPtr.Zero)
                return offsetX;

            var info = MONITORINFO.Create();
            if (!User32.GetMonitorInfo(monitor, ref info))
                return offsetX;

            var m = info.rcMonitor;
            if (m.right - m.left < widgetHostWidth)
                return offsetX;

            int screenX = taskbarRect.left + offsetX;
            screenX = Math.Clamp(screenX, m.left, m.right - widgetHostWidth);
            return screenX - taskbarRect.left;
        }

        private bool TryGetLayoutRects(
            out RECT taskbarScreenRect,
            out RECT notificationScreenRect,
            out RECT barScreenRect,
            out bool hasNotificationArea)
        {
            notificationScreenRect = default;
            barScreenRect = default;
            hasNotificationArea = false;

            if (!User32.GetWindowRect(hwndShell, out taskbarScreenRect)
                || taskbarScreenRect.right <= taskbarScreenRect.left
                || taskbarScreenRect.bottom <= taskbarScreenRect.top)
            {
                return false;
            }

            if (hwndTrayNotify != IntPtr.Zero
                && User32.IsWindow(hwndTrayNotify)
                && User32.GetWindowRect(hwndTrayNotify, out var trayRect)
                && trayRect.right > trayRect.left
                && trayRect.bottom > trayRect.top)
            {
                notificationScreenRect = GetNotificationAreaScreenRect(taskbarScreenRect, trayRect);
                hasNotificationArea = true;
            }
            else
            {
                int edge = IsRtlUI ? taskbarScreenRect.left : taskbarScreenRect.right;
                notificationScreenRect = new RECT
                {
                    left = edge,
                    top = taskbarScreenRect.top,
                    right = edge,
                    bottom = taskbarScreenRect.bottom,
                };
            }

            if (hwndReBar != IntPtr.Zero
                && User32.IsWindow(hwndReBar)
                && User32.GetWindowRect(hwndReBar, out var rebarRect)
                && rebarRect.right > rebarRect.left
                && rebarRect.bottom > rebarRect.top)
            {
                barScreenRect = rebarRect;
            }
            else
            {
                barScreenRect = taskbarScreenRect;
            }

            return true;
        }

        private static RECT ToTaskbarClientRect(RECT rect, RECT taskbarScreenRect)
            => new()
            {
                left = rect.left - taskbarScreenRect.left,
                top = rect.top - taskbarScreenRect.top,
                right = rect.right - taskbarScreenRect.left,
                bottom = rect.bottom - taskbarScreenRect.top,
            };

        private RECT GetNotificationAreaScreenRect(RECT taskbarScreenRect, RECT trayNotifyScreenRect)
        {
            var result = trayNotifyScreenRect;
            IncludeTaskbarChildBounds("ClockButton", taskbarScreenRect, ref result);
            IncludeTaskbarChildBounds("TrayClockWClass", taskbarScreenRect, ref result);
            IncludeTaskbarChildBounds("TrayShowDesktopButtonWClass", taskbarScreenRect, ref result);
            return result;
        }

        private void IncludeTaskbarChildBounds(string className, RECT taskbarScreenRect, ref RECT result)
        {
            for (var child = User32.FindWindowEx(hwndShell, IntPtr.Zero, className, null);
                 child != IntPtr.Zero;
                 child = User32.FindWindowEx(hwndShell, child, className, null))
            {
                if (!User32.GetWindowRect(child, out var bounds)
                    || bounds.right <= bounds.left
                    || bounds.bottom <= bounds.top
                    || !RectsIntersect(bounds, taskbarScreenRect))
                {
                    continue;
                }

                result = Union(result, bounds);
            }
        }

        private static bool RectsIntersect(RECT a, RECT b)
            => a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;

        private static RECT Union(RECT a, RECT b)
            => new()
            {
                left = Math.Min(a.left, b.left),
                top = Math.Min(a.top, b.top),
                right = Math.Max(a.right, b.right),
                bottom = Math.Max(a.bottom, b.bottom),
            };

        // Carries per-pass options through the EnumChildWindows callback via its GCHandle lParam, so the
        // scan is reentrancy-safe (UpdatePositionImpl runs on background threads while a drag runs on the
        // UI thread) instead of relying on a shared field.
        private sealed class BandEnumContext
        {
            public readonly List<RECT> List = new();
            public bool IncludeAppButtons;
            public bool ExcludeContainer;
        }

        private List<RECT> GetTaskbarItemBandWindows(bool includeAppButtons = true, bool excludeContainer = false)
        {
            var ctx = new BandEnumContext { IncludeAppButtons = includeAppButtons, ExcludeContainer = excludeContainer };
            var gc = GCHandle.Alloc(ctx);
            try { User32.EnumChildWindows(hwndShell, EnumTaskbarItemBandWindow, GCHandle.ToIntPtr(gc)); }
            finally { gc.Free(); }
            return ctx.List;
        }

        private static bool EnumTaskbarItemBandWindow(IntPtr hWnd, IntPtr lParam)
        {
            if (GCHandle.FromIntPtr(lParam).Target is not BandEnumContext ctx)
                return true;

            var builder = new StringBuilder(256);
            User32.GetClassName(hWnd, builder, builder.Capacity);
            string className = builder.ToString();
            // MSTaskSwWClass / MSTaskListWClass are the running-app buttons (volatile width). Skip them when
            // computing the stable resting position so opening/focusing an app never nudges the widget.
            bool isVolatileAppButton = className is "MSTaskSwWClass" or "MSTaskListWClass";
            if (isVolatileAppButton && !ctx.IncludeAppButtons)
                return true;
            // ReBarWindow32 is the container spanning the whole item area — excluded for gap solving so it
            // doesn't swallow every free gap; still included for the legacy forbidden-band callers.
            if (className == "ReBarWindow32" && ctx.ExcludeContainer)
                return true;
            if (className is not ("Start" or "TrayDummySearchControl" or "ReBarWindow32" or "MSTaskSwWClass" or "MSTaskListWClass"))
                return true;

            if (!User32.GetWindowRect(hWnd, out RECT bounds))
                return true;
            int width = bounds.right - bounds.left;
            int height = bounds.bottom - bounds.top;
            if (width <= 0 || height <= 0)
                return true;

            ctx.List.Add(bounds);
            return true;
        }

        private int LoadCustomPosition()
        {
            try
            {
                if (!File.Exists(positionPath))
                    return -1;
                if (!int.TryParse(File.ReadAllText(positionPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset))
                    return -1;

                // A saved offset of 0 lands under the Start button on LTR taskbars and looks "missing".
                return offset == 0 ? -1 : offset;
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


        private IntPtr CreateHostWindow(IntPtr parent)
        {
            RegisterWindowClass();
            return User32.CreateWindowEx(
                WindowStylesExtended.WS_EX_LAYERED, WidgetClassName, "WidgetHost",
                WindowStyles.WS_POPUP, 0, 0, 0, 0, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        private void RegisterWindowClass()
        {
            lock (WindowClassLock)
            {
                if (!windowClassRegistered)
                {
                    var wndClass = new WNDCLASSEX
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                        hInstance = Kernel32.GetModuleHandle(null),
                        lpfnWndProc = SharedWndProc,
                        lpszClassName = WidgetClassName,
                    };
                    if (User32.RegisterClassEx(ref wndClass) == 0)
                    {
                        // A prior UnregisterClass may have failed while a window still held the class, in
                        // which case it is still registered and usable — that is not a failure to create.
                        int error = Marshal.GetLastWin32Error();
                        if (error != ERROR_CLASS_ALREADY_EXISTS)
                            throw new Win32Exception(error, "Could not register the taskbar widget window class.");
                    }
                    windowClassRegistered = true;
                }

                windowClassUsers++;
                windowClassAcquired = true;
            }
        }

        private static IntPtr SharedWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
            => User32.DefWindowProc(hWnd, uMsg, wParam, lParam);

        private void ReleaseWindowClass()
        {
            lock (WindowClassLock)
            {
                if (!windowClassAcquired)
                    return;

                windowClassAcquired = false;
                windowClassUsers--;
                if (windowClassUsers == 0 && windowClassRegistered)
                {
                    // Clear the flag even when UnregisterClass fails. Leaving it set with zero users makes
                    // the next RegisterWindowClass skip registration and hand out a class that may no longer
                    // exist, so every later widget creation fails until the app restarts. Re-registering an
                    // already-registered class is a recoverable no-op; the reverse is not.
                    if (!User32.UnregisterClass(WidgetClassName, Kernel32.GetModuleHandle(null)))
                        Log.Warning("Could not unregister the taskbar widget window class; will re-register on next use");
                    windowClassRegistered = false;
                }
            }
        }

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

            static bool IsSystemWindow(string c) => c is "Start" or "TrayDummySearchControl" or "ReBarWindow32" or "WorkerW"
                or "TrayNotifyWnd" or "TrayButton" or "DynamicContent2"
                or "Windows.UI.Core.CoreWindow" or "Windows.UI.Composition.DesktopWindowContentBridge";
        }

        public void Dispose()
        {
            if (disposedValue)
                return;

            disposedValue = true;
            initialized = false;
            isVisible = false;
            positionUpdateCancellation.Cancel();
            try { appWindow?.Hide(); } catch { }
            if (widgetSummary is not null)
            {
                widgetSummary.PointerPressed -= WidgetSummary_PointerPressed;
                widgetSummary.DesiredHostWidthChanged -= WidgetSummary_DesiredHostWidthChanged;
                widgetSummary.PointerMoved -= WidgetSummary_PointerMoved;
                widgetSummary.PointerReleased -= WidgetSummary_PointerReleased;
                widgetSummary.PointerCanceled -= WidgetSummary_PointerCanceled;
            }
            _ = CompleteDisposeAfterPositionUpdatesAsync();
            GC.SuppressFinalize(this);
        }

        private async Task CompleteDisposeAfterPositionUpdatesAsync()
        {
            var gateWait = positionUpdateGate.WaitAsync();
            if (await Task.WhenAny(gateWait, Task.Delay(PositionDisposeWait)) != gateWait)
            {
                // A cross-process UIA call can stall while Explorer is rebuilding. The canceled update checks
                // its token before touching AppWindow again, so release the hidden window/XAML resources now
                // and defer only the watcher/COM cleanup until that call returns.
                Log.Warning($"Taskbar position update did not stop within {PositionDisposeWait.TotalSeconds:0}s; deferring watcher cleanup");
                DisposeWindowResources();
                ReleaseWindowClass();
                _ = DisposeWatcherAfterPositionUpdateAsync(gateWait);
                return;
            }

            try
            {
                DisposeWindowResources();
                try { taskbarWatcher.Dispose(); }
                catch (Exception ex) { Log.Warning(ex, "Failed to dispose the taskbar watcher"); }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Failed to finish disposing taskbar widget for taskbar=0x{hwndShell.ToInt64():X}");
            }
            finally
            {
                ReleaseWindowClass();
                positionUpdateGate.Release();
                DisposeSynchronizationPrimitives();
            }
        }

        private async Task DisposeWatcherAfterPositionUpdateAsync(Task gateWait)
        {
            try
            {
                await gateWait;
                try { taskbarWatcher.Dispose(); }
                catch (Exception ex) { Log.Warning(ex, "Failed to dispose the deferred taskbar watcher"); }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed while waiting to dispose the deferred taskbar watcher");
            }
            finally
            {
                positionUpdateGate.Release();
                DisposeSynchronizationPrimitives();
            }
        }

        /// <summary>
        /// Releases the cancellation source and gate. Called only from the two terminal dispose paths,
        /// after the final Release, since a stalled UpdatePositionImpl reads positionUpdateCancellation.Token
        /// and would throw ObjectDisposedException if these were freed in Dispose itself. Without this each
        /// widget recreation (DPI change, monitor plug, Explorer restart) leaked both handles.
        /// </summary>
        private void DisposeSynchronizationPrimitives()
        {
            try { positionUpdateCancellation.Dispose(); } catch { }
            try { positionUpdateGate.Dispose(); } catch { }
        }

        private void DisposeWindowResources()
        {
            try
            {
                if (!destroyed)
                    appWindow?.Destroy();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to destroy the taskbar widget window");
            }
            try { host?.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "Failed to dispose the taskbar XAML host"); }
            appWindow = null;
            host = null;
            hostContent = null;
            widgetSummary = null;
        }
    }
}
