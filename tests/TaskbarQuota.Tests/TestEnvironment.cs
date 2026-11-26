using System.Runtime.CompilerServices;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

internal static class TestEnvironment
{
    internal static string AppDataDirectory { get; } = Directory.CreateTempSubdirectory("TaskbarQuota.Tests-").FullName;
    private static readonly string TemporaryParent = Directory.GetParent(AppDataDirectory)!.FullName;
    private static IDisposable? _storageOverride;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Establish storage before any service captures an eager path or loads persisted settings.
        _storageOverride = AppStorage.OverrideAppDataDirectoryForTesting(AppDataDirectory);
        PricingEngine.UseBundledCatalogForTesting();
        UsageHistoryService.DiscoverFilesOverrideForTesting = static _ => Array.Empty<string>();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static void Cleanup()
    {
        // Never restore the real profile: late callbacks must still resolve to the isolated root.
        GC.KeepAlive(_storageOverride);
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(AppDataDirectory));
            if (directory.Exists
                && string.Equals(directory.Parent?.FullName, TemporaryParent, StringComparison.OrdinalIgnoreCase)
                && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                directory.Delete(recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
