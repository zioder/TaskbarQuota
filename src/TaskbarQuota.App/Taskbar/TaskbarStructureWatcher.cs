using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using TaskbarQuota.Interop;

namespace TaskbarQuota.Taskbar
{
   
    internal sealed class TaskbarStructureWatcher : IDisposable
    {
        private readonly IntPtr hwndTaskbar;
        private readonly IntPtr hwndReBar;
        private Timer? _timer;

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

        /// <summary>No UIAutomation available — widgets-button bounds are unknown; caller falls back to tray edge.</summary>
        public Task<RECT?> GetWidgetsButtonRectAsync() => Task.FromResult<RECT?>(null);

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
