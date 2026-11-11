using System.Runtime.CompilerServices;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

internal static class PricingTestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize() => PricingEngine.UseBundledCatalogForTesting();
}
