using System;
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
                    return null;

                var condition = _automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_AutomationIdPropertyId, WidgetsButtonAutomationId);
                var button = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
                if (button is null)
                    return null;

                var r = button.CurrentBoundingRectangle;
                if (r.right <= r.left || r.bottom <= r.top)
                    return null;

                return new RECT { left = r.left, top = r.top, right = r.right, bottom = r.bottom };
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug($"widgets-button UIA lookup failed: {ex.Message}");
                _automation = null;
                return null;
            }
        }

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
