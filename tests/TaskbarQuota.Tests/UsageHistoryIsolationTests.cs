using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

[Collection(WidgetRowSettingsCollection.Name)]
public sealed class UsageHistoryIsolationTests
{
    public static IEnumerable<object[]> Providers
        => Enum.GetValues<ProviderId>().Select(provider => new object[] { provider });

    [Theory]
    [MemberData(nameof(Providers))]
    public void TryLoadUsesExplicitDiscoveryWithoutReadingHostHistory(ProviderId provider)
    {
        var original = UsageHistoryService.DiscoverFilesOverrideForTesting;
        Assert.NotNull(original);
        Assert.Empty(original(provider));
        var calls = 0;
        try
        {
            UsageHistoryService.DiscoverFilesOverrideForTesting = id =>
            {
                Assert.Equal(provider, id);
                calls++;
                return Array.Empty<string>();
            };

            Assert.False(UsageHistoryService.TryLoad(provider, out var history));
            Assert.Equal(1, calls);
            Assert.Null(history.Today);
            Assert.Null(history.Last90Days);
        }
        finally
        {
            UsageHistoryService.DiscoverFilesOverrideForTesting = original;
        }
    }
}
