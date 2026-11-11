using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

[Collection(WidgetRowSettingsCollection.Name)]
public sealed class PricingIsolationTests
{
    [Fact]
    public void BundledCatalogIgnoresUserCachesAndOverridesWithoutWritingRefreshState()
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TaskbarQuota-pricing-" + Guid.NewGuid().ToString("N")));
        var pricingDirectory = Path.Combine(directory, "pricing");
        Directory.CreateDirectory(pricingDirectory);
        try
        {
            using var storageOverride = AppStorage.OverrideAppDataDirectoryForTesting(directory);
            const string poisonedCatalog = "{\"models\":{\"GLM-5.2\":{\"i\":0,\"o\":0}}}";
            const string poisonedOverrides = "{\"GLM-5.2\":{\"input_per_million\":0,\"output_per_million\":0}}";
            File.WriteAllText(Path.Combine(pricingDirectory, "litellm.json"), poisonedCatalog);
            File.WriteAllText(Path.Combine(pricingDirectory, "modelsdev.json"), poisonedCatalog);
            File.WriteAllText(Path.Combine(pricingDirectory, "overrides.json"), poisonedOverrides);

            var rates = new PricingCatalogStore(bundledOnly: true).Current().Resolve("GLM-5.2");
            var engineRates = PricingEngine.Resolve("GLM-5.2");

            Assert.NotNull(rates);
            Assert.InRange(rates.InputPerMillion, 0.01, 10);
            Assert.InRange(rates.OutputPerMillion, 0.01, 20);
            Assert.NotNull(engineRates);
            Assert.Equal(rates.InputPerMillion, engineRates.InputPerMillion);
            Assert.Equal(rates.OutputPerMillion, engineRates.OutputPerMillion);
            Assert.Equal(new[] { "litellm.json", "modelsdev.json", "overrides.json" },
                Directory.GetFiles(pricingDirectory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(poisonedCatalog, File.ReadAllText(Path.Combine(pricingDirectory, "litellm.json")));
            Assert.Equal(poisonedCatalog, File.ReadAllText(Path.Combine(pricingDirectory, "modelsdev.json")));
            Assert.Equal(poisonedOverrides, File.ReadAllText(Path.Combine(pricingDirectory, "overrides.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
