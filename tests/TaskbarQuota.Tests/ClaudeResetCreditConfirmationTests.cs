using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class ClaudeResetCreditConfirmationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task FreshClaudeGrantsSurviveTransientServiceFailureWithoutRenewingConfirmation(int resetsLeft)
    {
        var before = DateTimeOffset.UtcNow;
        var fetch = ParseGrants(DateTimeOffset.UtcNow.AddDays(1), resetsLeft);
        var confirmedAt = Assert.IsType<DateTimeOffset>(fetch.Usage.ResetCredits!.ConfirmedAt);
        Assert.InRange(confirmedAt, before, DateTimeOffset.UtcNow);
        var provider = new FixtureProvider(fetch);
        var service = new UsageService();
        service.Register(provider);

        var live = await service.FetchAsync(provider.Id, force: true).WaitAsync(TimeSpan.FromSeconds(10));
        var fallback = await service.FetchAsync(provider.Id, force: true).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(UsageObservationOrigin.FailureFallback, fallback.ObservationOrigin);
        Assert.Equal(resetsLeft, fallback.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.True(fallback.Fetch.Usage.ResetCredits.IsCached);
        Assert.Equal(confirmedAt, fallback.Fetch.Usage.ResetCredits.ConfirmedAt);
        Assert.Equal(confirmedAt, live.Fetch!.Usage.ResetCredits!.ConfirmedAt);
        Assert.False(live.Fetch.Usage.ResetCredits.IsCached);
    }

    [Fact]
    public void FreshClaudeGrantsSurviveSnapshotRestoreWithOriginalConfirmation()
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbq-claude-grants-" + Guid.NewGuid().ToString("N")));
        try
        {
            var fetch = ParseGrants(DateTimeOffset.UtcNow.AddDays(1));
            var provider = new FixtureProvider(fetch);
            var live = UsageResult.Success(provider.Id, provider, fetch)
                .AsLiveObservation(1, DateTimeOffset.Now);
            UsageSnapshotStore.Save(directory, new Dictionary<ProviderId, UsageResult> { [provider.Id] = live });

            var restored = Assert.Contains(provider.Id, UsageSnapshotStore.Load(directory, _ => provider));

            Assert.True(restored.IsStale);
            Assert.Equal(2, restored.Fetch!.Usage.ResetCredits!.AvailableCount);
            Assert.True(restored.Fetch.Usage.ResetCredits.IsCached);
            Assert.Equal(fetch.Usage.ResetCredits!.ConfirmedAt, restored.Fetch.Usage.ResetCredits.ConfirmedAt);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReusedClaudeGrantsKeepOriginalTtlAndGrantExpiry()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var fetch = ParseGrants(expiresAt);
        var confirmedAt = Assert.IsType<DateTimeOffset>(fetch.Usage.ResetCredits!.ConfirmedAt);
        var provider = new FixtureProvider(fetch);
        var live = UsageResult.Success(provider.Id, provider, fetch)
            .AsLiveObservation(1, confirmedAt);
        var reused = live.AsMemoryCache().AsLiveObservation(2, confirmedAt.AddSeconds(30));
        var fallback = reused.AsFailureFallback(3, confirmedAt.AddSeconds(45));
        var credits = Assert.IsType<ResetCreditsSnapshot>(fallback.Fetch!.Usage.ResetCredits);

        Assert.Equal(confirmedAt, credits.ConfirmedAt);
        Assert.Equal(2, credits.AvailableCount);
        Assert.Equal(0, credits.CurrentAt(expiresAt, asCached: true)!.AvailableCount);
        Assert.Empty(credits.CurrentAt(expiresAt, asCached: true)!.Credits);
        Assert.Null(credits.CurrentAt(confirmedAt + ResetCreditsSnapshot.MaxCacheAge, asCached: true));
        Assert.Equal(2, fetch.Usage.ResetCredits.AvailableCount);
    }

    private static ProviderFetchResult ParseGrants(DateTimeOffset expiresAt, int resetsLeft = 2)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            cedar_ember = new
            {
                eligible = true,
                grants = new[] { new { resets_left = resetsLeft, ends_at = expiresAt } },
            },
        }));
        var fetch = ClaudeProvider.BuildResultForTesting(doc.RootElement,
            new ClaudeProvider.Credentials("synthetic-token", "pro", "default_claude_ai"));
        fetch.Usage.UsageHistory = new UsageHistory();
        return fetch;
    }

    private sealed class FixtureProvider(ProviderFetchResult fetch) : IUsageProvider
    {
        private int _calls;
        public ProviderId Id => ProviderId.Claude;
        public string DisplayName => "Claude fixture";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => Interlocked.Increment(ref _calls) == 1
                ? Task.FromResult(fetch)
                : Task.FromException<ProviderFetchResult>(new ProviderException(ProviderErrorKind.Other, "Synthetic failure"));
    }
}
