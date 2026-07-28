using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

[Collection(ProviderInstallDetectorTestCollection.Name)]
public sealed class ProviderInstallDetectorEligibilityTests
{
    [Fact]
    public void ZaiApiKeyMakesProviderEligibleBeforeCliWarmup()
        => WithEnvironmentVariable(
            "Z_AI_API_KEY",
            "test-key",
            () => Assert.True(ProviderInstallDetector.IsInstalled(ProviderId.Zai)));

    [Fact]
    public void KimiApiKeyMakesProviderEligibleBeforeCliWarmup()
        => WithEnvironmentVariable(
            "KIMI_CODE_API_KEY",
            "test-key",
            () => Assert.True(ProviderInstallDetector.IsInstalled(ProviderId.Kimi)));

    [Fact]
    public void UnknownProviderValueIsNotAssumedInstalled()
    {
        var originalOverride = ProviderInstallDetector.IsInstalledOverrideForTesting;
        try
        {
            ProviderInstallDetector.IsInstalledOverrideForTesting = null;
            ProviderInstallDetector.ResetCliCacheForTesting();
            Assert.False(ProviderInstallDetector.IsInstalled((ProviderId)999));
        }
        finally
        {
            ProviderInstallDetector.IsInstalledOverrideForTesting = originalOverride;
            ProviderInstallDetector.ResetCliCacheForTesting();
        }
    }

    private static void WithEnvironmentVariable(string name, string value, Action assertion)
    {
        string? original = Environment.GetEnvironmentVariable(name);
        var originalOverride = ProviderInstallDetector.IsInstalledOverrideForTesting;
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            ProviderInstallDetector.IsInstalledOverrideForTesting = null;
            ProviderInstallDetector.ResetCliCacheForTesting();
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
            ProviderInstallDetector.IsInstalledOverrideForTesting = originalOverride;
            ProviderInstallDetector.ResetCliCacheForTesting();
        }
    }
}
