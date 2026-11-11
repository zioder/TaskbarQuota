using TaskbarQuota.Usage;
using TaskbarQuota.Controls;

namespace TaskbarQuota.Tests;

public class UsageSnapshotSemanticChangeTests
{
    [Theory]
    [InlineData("primary-presence")]
    [InlineData("window-label")]
    [InlineData("window-cost-value")]
    [InlineData("pricing-period")]
    [InlineData("pricing-multiplier")]
    [InlineData("additional-units")]
    public async Task SemanticChangesArePublishedEvenWhenNumericQuotaDoesNotChange(string change)
    {
        var firstUsage = Snapshot();
        var nextUsage = Snapshot(change);
        var firstAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var nextAt = firstAt.AddSeconds(30);
        var service = new UsageService();
        service.Register(new TestProvider(
            new ProviderFetchResult(firstUsage, "test", firstAt),
            new ProviderFetchResult(nextUsage, "test", nextAt)));

        var first = await service.FetchAsync(ProviderId.Copilot, force: true);
        var next = await service.FetchAsync(ProviderId.Copilot, force: true);

        Assert.Equal(firstAt, first.Fetch!.FetchedAt);
        Assert.Equal(nextAt, next.Fetch!.FetchedAt);
        var actual = next.Fetch.Usage;
        Assert.Equal(firstUsage.Primary.UsedPercent, actual.Primary.UsedPercent);
        Assert.Equal(nextUsage.HasPrimaryWindow, actual.HasPrimaryWindow);
        Assert.Equal(nextUsage.Primary.Label, actual.Primary.Label);
        Assert.Equal(nextUsage.Primary.ShowCostValue, actual.Primary.ShowCostValue);
        Assert.Equal(nextUsage.Pricing!.Period, actual.Pricing!.Period);
        Assert.Equal(nextUsage.Pricing.Multiplier, actual.Pricing.Multiplier);
        Assert.Equal(nextUsage.AdditionalUsage!.IsCredits, actual.AdditionalUsage!.IsCredits);
    }

    [Theory]
    [InlineData("primary-presence")]
    [InlineData("window-label")]
    [InlineData("window-cost-value")]
    [InlineData("window-minutes")]
    [InlineData("window-reset-at")]
    [InlineData("pricing-period")]
    [InlineData("pricing-multiplier")]
    [InlineData("additional-units")]
    public void SemanticChangesInvalidateTheRenderedWidget(string change)
    {
        var provider = new TestProvider();
        var before = UsageResult.Success(provider.Id, provider, new ProviderFetchResult(Snapshot(), "test"));
        var after = UsageResult.Success(provider.Id, provider, new ProviderFetchResult(Snapshot(change), "test"));

        Assert.NotEqual(WidgetSummary.BuildRenderSignature(before), WidgetSummary.BuildRenderSignature(after));
    }

    private static UsageSnapshot Snapshot(string? change = null) => new(new RateWindow(34)
    {
        Label = change == "window-label" ? "Weekly" : "Session",
        ShowCostValue = change == "window-cost-value",
        WindowMinutes = change == "window-minutes" ? 10080 : null,
        ResetAt = change == "window-reset-at" ? new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) : null,
    })
    {
        HasPrimaryWindow = change != "primary-presence",
        Pricing = new UsagePricingSnapshot(change == "pricing-period" ? "Peak" : "Daily",
            change == "pricing-multiplier" ? 2 : 1),
        AdditionalUsage = new AdditionalUsageSnapshot { Enabled = true, SpentUsd = 3, BudgetUsd = 10, IsCredits = change == "additional-units" },
    };

    private sealed class TestProvider(params ProviderFetchResult[] results) : IUsageProvider
    {
        private int _calls;
        public ProviderId Id => ProviderId.Copilot;
        public string DisplayName => "Copilot";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => Task.FromResult(results[Interlocked.Increment(ref _calls) - 1]);
    }
}
