using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;

namespace TaskbarQuota.Interop
{
    public static class SystemInfos
    {
        private const int Windows11_Min_BuildNumber = 22000;
        private static readonly int osBuildNumber = GetOSBuildNumber();

        public static bool IsWindows11_OrLater => osBuildNumber >= Windows11_Min_BuildNumber;

        public static RECT GetTaskBarBounds()
        {
            APPBARDATA data = new APPBARDATA();
            data.cbSize = Marshal.SizeOf(data);
            Shell32.SHAppBarMessage(AppBarMessage.ABM_GETTASKBARPOS, ref data);
            return data.rc;
        }

        public static bool IsTaskBarCentered()
        {
            if (IsWindows11_OrLater)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (key != null)
                {
                    int value = Convert.ToInt32(key.GetValue("TaskbarAl", 1));
                    return value == 1;
                }
                return true;
            }
            return false;
        }

        public static bool IsTaskBarWidgetsEnabled()
        {
            if (IsWindows11_OrLater)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (key != null)
                {
                    int value = Convert.ToInt32(key.GetValue("TaskbarDa", 1));
                    return value == 1;
                }
                return true;
            }
            return false;
        }

        /// <summary>SystemUsesLightTheme governs the taskbar/Start theme (vs. AppsUseLightTheme for app surfaces).</summary>
        public static bool? IsSystemLightThemeUsed()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                int value = Convert.ToInt32(key.GetValue("SystemUsesLightTheme", 0));
                return value != 0;
            }
            return null;
        }

        public static bool? IsAppsLightThemeUsed()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                int value = Convert.ToInt32(key.GetValue("AppsUseLightTheme", 0));
                return value != 0;
            }
            return null;
        }

        private static int GetOSBuildNumber()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var osBuildNumberValue = key?.GetValue("CurrentBuildNumber");
            return osBuildNumberValue != null ? Convert.ToInt32(osBuildNumberValue) : 0;
        }
    }
}
