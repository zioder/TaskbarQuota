using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class ResetCreditObservationTimeTests
{
    [Fact]
    public void LiveSnapshotUsesObservationTimeWhileLaterCacheReadsExpireCredits()
    {
        var observedAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var credits = new ResetCreditsSnapshot(1,
            [new ResetCreditGrant("available", observedAt.AddDays(-1), observedAt.AddMinutes(5))],
            confirmedAt: observedAt);
        var snapshot = new UsageSnapshot(new RateWindow(10)) { ResetCredits = credits };
        var result = UsageResult.Success(ProviderId.Codex, new TestProvider(), new ProviderFetchResult(snapshot, "test"))
            .AsLiveObservation(1, observedAt);

        Assert.Equal(1, result.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.Equal(observedAt, result.Fetch.Usage.ResetCredits.ConfirmedAt);
        Assert.Null(result.AsMemoryCache().Fetch!.Usage.ResetCredits);
        Assert.Equal(1, result.Fetch.Usage.ResetCredits.AvailableCount);
    }

    private sealed class TestProvider : IUsageProvider
    {
        public ProviderId Id => ProviderId.Codex;
        public string DisplayName => "Codex";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
