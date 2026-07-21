using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Interop;

namespace TaskbarQuota.Taskbar
{
    internal readonly record struct TaskbarWindowTarget(IntPtr Handle, bool IsPrimary, string DisplayKey)
    {
        internal const string PrimaryClassName = "Shell_TrayWnd";
        internal const string SecondaryClassName = "Shell_SecondaryTrayWnd";

        public static bool TryFindAll(out IReadOnlyList<TaskbarWindowTarget> result)
        {
            var targets = new List<TaskbarWindowTarget>();
            var gc = GCHandle.Alloc(targets);
            bool success;
            try
            {
                success = User32.EnumWindows(EnumTaskbarWindow, GCHandle.ToIntPtr(gc));
            }
            finally
            {
                gc.Free();
            }

            targets.Sort(CompareTargets);
            result = targets;
            return success;
        }

        public string GetPositionPath()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TaskbarQuota");
            string path = Path.Combine(directory, BuildPositionFileName(DisplayKey));

            if (IsPrimary)
                return MigrateLegacyPrimaryPosition(directory, path);

            return path;
        }

        internal static bool IsTaskbarClassName(string className, out bool isPrimary)
        {
            isPrimary = string.Equals(className, PrimaryClassName, StringComparison.Ordinal);
            return isPrimary || string.Equals(className, SecondaryClassName, StringComparison.Ordinal);
        }

        internal static string BuildDisplayKey(string? displayId, RECT bounds)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(displayId))
            {
                foreach (char c in displayId)
                {
                    if (char.IsLetterOrDigit(c) || c is '-' or '_')
                        builder.Append(c);
                }
            }

            return builder.Length > 0
                ? builder.ToString()
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{bounds.left}_{bounds.top}_{bounds.right}_{bounds.bottom}");
        }

        internal static string BuildPositionFileName(string displayKey)
            => $"taskbar-widget-position-{displayKey}.txt";

        private static bool EnumTaskbarWindow(IntPtr hwnd, IntPtr lParam)
        {
            var builder = new StringBuilder(64);
            User32.GetClassName(hwnd, builder, builder.Capacity);
            if (IsTaskbarClassName(builder.ToString(), out bool isPrimary)
                && User32.IsWindow(hwnd)
                && GCHandle.FromIntPtr(lParam).Target is List<TaskbarWindowTarget> targets)
            {
                var bounds = GetBounds(hwnd);
                targets.Add(new TaskbarWindowTarget(
                    hwnd,
                    isPrimary,
                    BuildDisplayKey(TryGetDisplayId(hwnd), bounds)));
            }

            return true;
        }

        private static int CompareTargets(TaskbarWindowTarget left, TaskbarWindowTarget right)
        {
            if (left.IsPrimary != right.IsPrimary)
                return left.IsPrimary ? -1 : 1;

            var leftBounds = GetBounds(left.Handle);
            var rightBounds = GetBounds(right.Handle);
            int byTop = leftBounds.top.CompareTo(rightBounds.top);
            return byTop != 0 ? byTop : leftBounds.left.CompareTo(rightBounds.left);
        }

        private static string TryGetDisplayId(IntPtr taskbarHandle)
        {
            var monitor = User32.MonitorFromWindow(taskbarHandle, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return string.Empty;

            var info = MONITORINFOEX.Create();
            return User32.GetMonitorInfo(monitor, ref info) ? info.szDevice : string.Empty;
        }

        private static RECT GetBounds(IntPtr hwnd)
            => User32.GetWindowRect(hwnd, out var bounds) ? bounds : default;

        private static string MigrateLegacyPrimaryPosition(string directory, string displayPositionPath)
        {
            string legacyPath = Path.Combine(directory, "taskbar-widget-position.txt");
            if (File.Exists(displayPositionPath) || !File.Exists(legacyPath))
                return displayPositionPath;

            try
            {
                Directory.CreateDirectory(directory);
                File.Move(legacyPath, displayPositionPath);
                Log.Information($"Migrated the primary taskbar widget position to {Path.GetFileName(displayPositionPath)}");
                return displayPositionPath;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not migrate the primary taskbar widget position");
                return legacyPath;
            }
        }
    }
}
