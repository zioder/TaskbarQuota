using System;
using System.IO;
using System.Threading;

namespace TaskbarQuota;

/// <summary>Paths under %LOCALAPPDATA% and one-time migration from the WinCheck folder name.</summary>
public static class AppStorage
{
    public const string AppFolderName = "TaskbarQuota";
    private const string LegacyAppFolderName = "WinCheck";

    private static string? _appDataDirectoryOverride;

    public static string AppDataDirectory => _appDataDirectoryOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    internal static IDisposable OverrideAppDataDirectoryForTesting(string directory)
    {
        var previous = _appDataDirectoryOverride;
        _appDataDirectoryOverride = directory;
        return new AppDataDirectoryOverride(() => _appDataDirectoryOverride = previous);
    }

    private sealed class AppDataDirectoryOverride(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }

    /// <summary>Copies files from %LOCALAPPDATA%\WinCheck when the new folder is empty.</summary>
    public static void MigrateLegacyDataIfNeeded()
    {
        var targetDir = AppDataDirectory;
        var legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyAppFolderName);

        if (!Directory.Exists(legacyDir))
            return;

        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(legacyDir))
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(targetDir, name);
            if (File.Exists(dest))
                continue;

            try
            {
                File.Copy(file, dest);
            }
            catch
            {
                // Best-effort migration; user can copy credentials manually.
            }
        }
    }
}
