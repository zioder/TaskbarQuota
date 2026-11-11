using System.Runtime.CompilerServices;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

internal static class UsageHistoryTestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
        => UsageHistoryService.DiscoverFilesOverrideForTesting = static _ => Array.Empty<string>();
}
