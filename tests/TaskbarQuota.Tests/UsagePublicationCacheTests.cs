using System.Threading;
using TaskbarQuota.Usage;
using Xunit;

namespace TaskbarQuota.Tests;

public class UsagePublicationCacheTests
{
    [Fact]
    public void IdenticalSnapshotsAreSuppressedUntilForce()
    {
        var cache = new UsagePublicationCache();
        var result = UsageResult.Failure(ProviderId.Codex, "test");

        Assert.True(cache.ShouldPublish(result, force: false));
        cache.RecordPublished(result);
        Assert.False(cache.ShouldPublish(result, force: false));
        Assert.True(cache.ShouldPublish(result, force: true));
    }

    [Fact]
    public void LocalHistoryChangeTriggersRepublish()
    {
        var cache = new UsagePublicationCache();
        var provider = new StubProvider();
        var usageA = new UsageSnapshot(new RateWindow(0))
        {
            UsageHistory = new UsageHistory
            {
                Daily = [new DailyUsage("2026-01-01", 100)],
            },
        };
        var usageB = new UsageSnapshot(new RateWindow(0))
        {
            UsageHistory = new UsageHistory
            {
                Daily = [new DailyUsage("2026-01-01", 200)],
            },
        };
        var first = UsageResult.Success(ProviderId.Codex, provider, new ProviderFetchResult(usageA, "local"));
        var second = UsageResult.Success(ProviderId.Codex, provider, new ProviderFetchResult(usageB, "local"));

        Assert.True(cache.ShouldPublish(first, force: false));
        cache.RecordPublished(first);
        Assert.True(cache.ShouldPublish(second, force: false));
    }

    [Fact]
    public void ResetCreditExpiryTriggersRepublishWithTheSameQuotaObservation()
    {
        var cache = new UsagePublicationCache();
        var provider = new StubProvider();
        var now = DateTimeOffset.UtcNow;
        var confirmedAt = now.AddMinutes(-1);
        var usage = new UsageSnapshot(new RateWindow(30))
        {
            ResetCredits = new ResetCreditsSnapshot(2,
            [
                new("available", confirmedAt, now.AddSeconds(-1)),
                new("available", confirmedAt, now.AddHours(1)),
            ], confirmedAt: confirmedAt),
        };
        var original = UsageResult.Success(provider.Id, provider, new ProviderFetchResult(usage, "test", confirmedAt))
            .AsLiveObservation(1, confirmedAt);
        var aged = original.AsMemoryCache();

        Assert.Equal(2, original.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.Equal(1, aged.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.Equal(original.ObservationSequence, aged.ObservationSequence);
        Assert.Equal(original.ObservedAt, aged.ObservedAt);
        Assert.Equal(original.Fetch.FetchedAt, aged.Fetch.FetchedAt);

        cache.RecordPublished(original);
        Assert.True(cache.ShouldPublish(aged, force: false));

        cache.RecordPublished(aged);
        Assert.False(cache.ShouldPublish(original.AsMemoryCache(), force: false));
    }

    private sealed class StubProvider : IUsageProvider
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
