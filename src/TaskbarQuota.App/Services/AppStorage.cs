using System;
using System.IO;

namespace TaskbarQuota;

/// <summary>Paths under %LOCALAPPDATA% and one-time migration from the WinCheck folder name.</summary>
public static class AppStorage
{
    public const string AppFolderName = "TaskbarQuota";
    private const string LegacyAppFolderName = "WinCheck";

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

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
