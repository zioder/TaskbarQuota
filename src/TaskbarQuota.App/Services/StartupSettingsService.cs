using System;
using Microsoft.Win32;

namespace TaskbarQuota;

public static class StartupSettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TaskbarQuota";
    private const string LegacyRunValueName = "WinCheck";
    public const string StartupArgument = "--startup-widget";

    /// <summary>Moves a WinCheck startup entry to TaskbarQuota after rename.</summary>
    public static void MigrateLegacyStartupEntryIfNeeded()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(LegacyRunValueName) is not string legacy
                || !legacy.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
            if (key.GetValue(RunValueName) is null)
                Apply(true);
        }
        catch
        {
        }
    }

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(RunValueName) is string value
                    && value.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (!enabled)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                return;

            key.SetValue(RunValueName, $"\"{executable}\" {StartupArgument}");
        }
        catch
        {
            // Startup registration is best-effort; the app itself should continue normally.
        }
    }
}
