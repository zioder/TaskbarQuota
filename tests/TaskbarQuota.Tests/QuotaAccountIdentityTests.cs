using TaskbarQuota.Services;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class QuotaAccountIdentityTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("shared@example.test")]
    public void AccountChangeIsNotReportedAsQuotaReplenishment(string? email)
    {
        var tracker = new QuotaReplenishmentTracker();
        Assert.Empty(tracker.Observe(Result(1, 88, "account-a", email), Now));
        Assert.Empty(tracker.Observe(Result(2, 40, "account-b", email), Now));
        Assert.Single(tracker.Observe(Result(3, 4, "account-b", email), Now));
    }

    [Fact]
    public void SameAccountStillReportsAnActualAvailabilityIncrease()
    {
        var tracker = new QuotaReplenishmentTracker();
        Assert.Empty(tracker.Observe(Result(1, 88, "account-a", null), Now));
        Assert.Single(tracker.Observe(Result(2, 4, "account-a", null), Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartComparesTheOpaqueAccountIdentityEvenWhenEmailsMatch(bool sameAccount)
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TaskbarQuota-quota-identity-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(directory, QuotaReplenishmentStateStore.FileName);
        try
        {
            var first = new QuotaReplenishmentCrossSessionTracker(new QuotaReplenishmentStateStore(path));
            Assert.Empty(first.Observe(Result(1, 88, "account-a", "shared@example.test"), Now));

            var second = new QuotaReplenishmentCrossSessionTracker(new QuotaReplenishmentStateStore(path));
            var events = second.Observe(Result(1, 40, sameAccount ? "account-a" : "account-b", "shared@example.test"), Now.AddMinutes(1));
            if (sameAccount) Assert.Single(events);
            else Assert.Empty(events);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static UsageResult Result(long sequence, double used, string identity, string? email)
    {
        var usage = new UsageSnapshot(new RateWindow(used, 300, Now.AddHours(1))) { Email = email };
        return UsageResult.Success(ProviderId.Codex, new TestProvider(),
            new ProviderFetchResult(usage, "test", Now, identity)).AsLiveObservation(sequence, Now);
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
