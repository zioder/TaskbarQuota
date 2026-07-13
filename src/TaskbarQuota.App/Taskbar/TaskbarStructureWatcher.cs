using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Interop.UIAutomationClient;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using TaskbarQuota.Interop;

namespace TaskbarQuota.Taskbar
{

    internal sealed class TaskbarStructureWatcher : IDisposable
    {
        private const string WidgetsButtonAutomationId = "WidgetsButton";

        private readonly IntPtr hwndTaskbar;
        private readonly IntPtr hwndReBar;
        private Timer? _timer;
        private IUIAutomation? _automation;
        private RECT? _lastWidgetsButtonRect;
        private DateTime _lastWidgetsButtonRectAt = DateTime.MinValue;
        private static readonly TimeSpan WidgetsButtonRectMaxAge = TimeSpan.FromSeconds(30);
        private List<RECT>? _lastTaskButtonRects;
        private DateTime _lastTaskButtonRectsAt = DateTime.MinValue;
        private static readonly TimeSpan TaskButtonRectsMaxAge = TimeSpan.FromSeconds(30);

        private bool widgetsButtonEnabled;
        private bool taskbarCentered;
        private bool taskbarHidden;

        public event EventHandler<TaskbarChangedEventArgs>? TaskbarChangedNotificationCompleted;

        public TaskbarStructureWatcher(IntPtr hwndTaskbar, IntPtr hwndReBar)
        {
            this.hwndTaskbar = hwndTaskbar;
            this.hwndReBar = hwndReBar;

            widgetsButtonEnabled = SystemInfos.IsTaskBarWidgetsEnabled();
            taskbarCentered = SystemInfos.IsTaskBarCentered();
            taskbarHidden = IsTaskbarHidden();

            _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        private void Poll()
        {
            try
            {
                bool isWidgets = SystemInfos.IsTaskBarWidgetsEnabled();
                bool isCentered = SystemInfos.IsTaskBarCentered();
                bool isHidden = IsTaskbarHidden();

                var reason = TaskbarChangeReason.Other;
                if (isWidgets != widgetsButtonEnabled) { reason = TaskbarChangeReason.WidgetsButton; widgetsButtonEnabled = isWidgets; }
                else if (isCentered != taskbarCentered) { reason = TaskbarChangeReason.Alignment; taskbarCentered = isCentered; }
                else if (isHidden != taskbarHidden) { reason = TaskbarChangeReason.Visibility; taskbarHidden = isHidden; }

                TaskbarChangedNotificationCompleted?.Invoke(this, new TaskbarChangedEventArgs
                {
                    Reason = reason,
                    IsTaskbarHidden = taskbarHidden,
                    IsTaskbarCentered = taskbarCentered,
                    IsTaskbarWidgetsEnabled = widgetsButtonEnabled,
                });
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Screen bounds (physical px) of the taskbar Widgets button via UI Automation, or null when it
        /// can't be found (disabled, or the tree isn't ready). Runs off the UI thread — the UIA cross-
        /// process walk can block. Lets the default anchor sit clear of the Widgets pill (issue #10).
        /// </summary>
        public Task<RECT?> GetWidgetsButtonRectAsync() => Task.Run(TryGetWidgetsButtonRect);

        private RECT? TryGetWidgetsButtonRect()
        {
            if (hwndTaskbar == IntPtr.Zero || !SystemInfos.IsTaskBarWidgetsEnabled())
                return null;

            try
            {
                _automation ??= new CUIAutomation();
                var root = _automation.ElementFromHandle(hwndTaskbar);
                if (root is null)
                    return CachedWidgetsButtonRect();

                var condition = _automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_AutomationIdPropertyId, WidgetsButtonAutomationId);
                var button = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
                if (button is null)
                    return CachedWidgetsButtonRect();

                var r = button.CurrentBoundingRectangle;
                if (r.right <= r.left || r.bottom <= r.top)
                    return CachedWidgetsButtonRect();

                var rect = new RECT { left = r.left, top = r.top, right = r.right, bottom = r.bottom };
                _lastWidgetsButtonRect = rect;
                _lastWidgetsButtonRectAt = DateTime.UtcNow;
                return rect;
            }
            catch (Exception ex)
            {
                // Cross-process UIA reads fail intermittently (shell busy, tree rebuilding). Returning null
                // here makes the caller anchor the widget at x=0 — right on top of the weather/Widgets pill.
                // Fall back to the last known-good rect so a transient failure never causes the overlap (#17).
                Diagnostics.Log.Debug($"widgets-button UIA lookup failed: {ex.Message}");
                _automation = null;
                return CachedWidgetsButtonRect();
            }
        }

        private RECT? CachedWidgetsButtonRect()
            => _lastWidgetsButtonRect is { } rect && DateTime.UtcNow - _lastWidgetsButtonRectAt < WidgetsButtonRectMaxAge
                ? rect
                : null;

        /// <summary>
        /// Screen bounds (physical px) of every Button in the taskbar UIA tree — the running-app icons plus
        /// system buttons (Start, Search, Widgets, tray). On Win11 the app icons are XAML, not classic
        /// MSTask* child windows, so an HWND scan can't see them; treating each button rect as an obstacle
        /// keeps the widget from ever landing on top of the app cluster (issue #17). Falls back to the last
        /// good set on transient UIA failure.
        /// </summary>
        public Task<List<RECT>?> GetTaskbarButtonRectsAsync() => Task.Run(TryGetTaskbarButtonRects);

        private List<RECT>? TryGetTaskbarButtonRects()
        {
            if (hwndTaskbar == IntPtr.Zero)
                return CachedTaskButtonRects();

            try
            {
                _automation ??= new CUIAutomation();
                var root = _automation.ElementFromHandle(hwndTaskbar);
                if (root is null)
                    return CachedTaskButtonRects();

                var condition = _automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_ButtonControlTypeId);
                var buttons = root.FindAll(TreeScope.TreeScope_Descendants, condition);
                if (buttons is null)
                    return CachedTaskButtonRects();

                var rects = new List<RECT>(buttons.Length);
                for (int i = 0; i < buttons.Length; i++)
                {
                    var r = buttons.GetElement(i).CurrentBoundingRectangle;
                    if (r.right > r.left && r.bottom > r.top)
                        rects.Add(new RECT { left = r.left, top = r.top, right = r.right, bottom = r.bottom });
                }

                _lastTaskButtonRects = rects;
                _lastTaskButtonRectsAt = DateTime.UtcNow;
                return rects;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"taskbar-button UIA scan failed: {ex.Message}");
                _automation = null;
                return CachedTaskButtonRects();
            }
        }

        private List<RECT>? CachedTaskButtonRects()
            => _lastTaskButtonRects is { } rects && DateTime.UtcNow - _lastTaskButtonRectsAt < TaskButtonRectsMaxAge
                ? rects
                : null;

        private bool IsTaskbarHidden()
        {
            IntPtr p = User32.GetProp(hwndTaskbar, "IsAutoHideEnabled");
            if (p != (IntPtr)1) return false;
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwndTaskbar);
            var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
            User32.GetWindowRect(hwndTaskbar, out var rect);
            return rect.bottom > area.OuterBounds.Height;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
            if (_automation is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_automation); } catch { }
                _automation = null;
            }
        }
    }

    public sealed class TaskbarChangedEventArgs : EventArgs
    {
        public TaskbarChangeReason Reason { get; init; }
        public bool IsTaskbarHidden { get; init; }
        public bool IsTaskbarCentered { get; init; }
        public bool IsTaskbarWidgetsEnabled { get; init; }
    }

    public enum TaskbarChangeReason { None, Alignment, Visibility, WidgetsButton, TabletMode, Other }
}
