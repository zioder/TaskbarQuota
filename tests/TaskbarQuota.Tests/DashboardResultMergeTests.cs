using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Tests;

public class DashboardResultMergeTests
{
    [Fact]
    public void FinalDashboardPassPreservesCoordinatorRefreshFromWhileItWasLoading()
    {
        var provider = new TestProvider(ProviderId.Codex);
        var dashboard = Success(provider, 1, 80);
        var coordinator = Success(provider, 2, 25);
        var other = Success(new TestProvider(ProviderId.Claude), 3, 40);
        var stages = new DashboardLoadStageGate();
        stages.Reserve();
        long final = stages.Reserve();

        // A coordinator callback publishes while the other provider's dashboard fetch is pending.
        IReadOnlyList<UsageResult> visible = DashboardResultMerge.KeepNewerObservations([coordinator], [dashboard]);
        Assert.True(stages.TryAccept(final));
        visible = DashboardResultMerge.KeepNewerObservations([dashboard, other], visible);

        Assert.Same(coordinator, visible[0]);
        Assert.Equal(25, visible[0].Fetch!.Usage.Primary.UsedPercent);
        Assert.Same(other, visible[1]);
    }

    [Fact]
    public void NewAuthenticationFailureReplacesSuccessAndLateSuccessCannotResurrectIt()
    {
        var provider = new TestProvider(ProviderId.Codex);
        var success = Success(provider, 1, 80);
        var failure = UsageResult.Failure(provider.Id, "Expired", provider, ProviderErrorKind.AuthRequired)
            .AsFailureFallback(2, DateTimeOffset.Now);

        var visible = DashboardResultMerge.KeepNewerObservations([failure], [success]);
        Assert.Same(failure, visible[0]);
        visible = DashboardResultMerge.KeepNewerObservations([success], visible);
        Assert.Same(failure, visible[0]);
        Assert.False(visible[0].Ok);
    }

    [Fact]
    public void InitialPendingCannotEraseConfirmedValuesButNewObservationAdvancesThem()
    {
        var provider = new TestProvider(ProviderId.Codex);
        var old = Success(provider, 1, 80);
        var pending = UsageResult.Pending(provider.Id, provider, "Loading...");
        var next = Success(provider, 2, 25);

        var visible = DashboardResultMerge.KeepNewerObservations([pending], [old]);
        Assert.Same(old, visible[0]);
        visible = DashboardResultMerge.KeepNewerObservations([next], visible);
        Assert.Same(next, visible[0]);
    }

    [Fact]
    public void FinalHistorySnapshotUsesNewerCoordinatorHistory()
    {
        var provider = new TestProvider(ProviderId.Codex);
        var old = Success(provider, 1, 80);
        old.Fetch!.Usage.UsageHistory = new UsageHistory { Today = new UsagePeriod(10) };
        var next = Success(provider, 2, 25);
        next.Fetch!.Usage.UsageHistory = new UsageHistory { Today = new UsagePeriod(20) };

        var visible = DashboardResultMerge.KeepNewerObservations([old], [next]);
        var historySnapshot = DashboardResultMerge.KeepNewerHistorySnapshots([old], visible);

        Assert.Equal((ulong)20, historySnapshot[0].Fetch!.Usage.UsageHistory!.Today!.Tokens);
    }

    [Theory]
    [InlineData(ProviderErrorKind.AuthRequired)]
    [InlineData(ProviderErrorKind.NotInstalled)]
    [InlineData(ProviderErrorKind.Other)]
    public void FinalHistorySnapshotPreservesLocalHistoryWhenLatestQuotaAttemptFails(ProviderErrorKind errorKind)
    {
        var provider = new TestProvider(ProviderId.Codex);
        var local = LocalHistory(provider);
        var failure = UsageResult.Failure(provider.Id, "Unavailable", provider, errorKind)
            .AsFailureFallback(2, DateTimeOffset.Now);

        var visible = DashboardResultMerge.KeepNewerObservations([failure], [local]);
        var historySnapshot = DashboardResultMerge.KeepNewerHistorySnapshots([local], visible);

        Assert.Same(failure, Assert.Single(visible));
        Assert.False(visible[0].Ok);
        Assert.Same(local, Assert.Single(historySnapshot));
        Assert.Equal((ulong)10, historySnapshot[0].Fetch!.Usage.UsageHistory!.Today!.Tokens);
    }

    [Fact]
    public void FinalHistorySnapshotPreservesLocalHistoryWhenNewerQuotaHasNoHistory()
    {
        var provider = new TestProvider(ProviderId.Codex);
        var local = LocalHistory(provider);
        var quota = Success(provider, 2, 25);

        var visible = DashboardResultMerge.KeepNewerObservations([quota], [local]);
        var historySnapshot = DashboardResultMerge.KeepNewerHistorySnapshots([local], visible);

        Assert.Same(quota, Assert.Single(visible));
        Assert.Equal(25, visible[0].Fetch!.Usage.Primary.UsedPercent);
        Assert.Same(local, Assert.Single(historySnapshot));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedHistorySurvivesSubsequentQuotaOnlyCompositionPass(bool quotaFailed)
    {
        var provider = new TestProvider(ProviderId.Codex);
        var local = LocalHistory(provider);
        var quota = quotaFailed
            ? UsageResult.Failure(provider.Id, "Expired", provider, ProviderErrorKind.AuthRequired)
                .AsFailureFallback(2, DateTimeOffset.Now)
            : Success(provider, 2, 25);
        // A completed history pass establishes the baseline used when a later composition/appearance
        // refresh recreates cards without performing another history scan.
        var lastHistoryResults = DashboardResultMerge.KeepNewerHistorySnapshots([local], [quota]);
        IReadOnlyList<UsageResult>? incomingHistory = null;
        lastHistoryResults = DashboardResultMerge.KeepNewerHistorySnapshots(
            incomingHistory ?? lastHistoryResults, [quota]);

        var historyOverrides = lastHistoryResults.ToDictionary(
            result => result.Id, result => result.Fetch!.Usage.UsageHistory!);
        Assert.Equal((ulong)10, historyOverrides[provider.Id].Today!.Tokens);
        Assert.Same(local, Assert.Single(lastHistoryResults));
        Assert.Equal(!quotaFailed, quota.Ok);
    }

    [Fact]
    public void CompletedEmptyHistoryPassReplacesEarlierBaseline()
    {
        var provider = new TestProvider(ProviderId.Codex);
        var local = LocalHistory(provider);
        var failure = UsageResult.Failure(provider.Id, "Expired", provider, ProviderErrorKind.AuthRequired)
            .AsFailureFallback(2, DateTimeOffset.Now);
        var lastHistoryResults = DashboardResultMerge.KeepNewerHistorySnapshots([local], [failure]);

        lastHistoryResults = DashboardResultMerge.KeepNewerHistorySnapshots([], [failure]);

        Assert.Empty(lastHistoryResults);
    }

    [Fact]
    public void FinalHistorySnapshotPreservesIncomingHistoryWhenCoordinatorHistoryIsOlder()
    {
        var provider = new TestProvider(ProviderId.Codex);
        var old = Success(provider, 1, 80);
        old.Fetch!.Usage.UsageHistory = new UsageHistory { Today = new UsagePeriod(10) };
        var next = Success(provider, 2, 25);
        next.Fetch!.Usage.UsageHistory = new UsageHistory { Today = new UsagePeriod(20) };

        var historySnapshot = DashboardResultMerge.KeepNewerHistorySnapshots([next], [old]);

        Assert.Same(next, Assert.Single(historySnapshot));
        Assert.Equal((ulong)20, historySnapshot[0].Fetch!.Usage.UsageHistory!.Today!.Tokens);
    }

    [Fact]
    public void EqualSequencePreservesIncomingMetadataAndDoesNotAddMissingProviders()
    {
        var provider = new TestProvider(ProviderId.Codex);
        var live = Success(provider, 1, 80);
        var cached = live.AsMemoryCache();
        var other = Success(new TestProvider(ProviderId.Claude), 2, 40);

        var visible = DashboardResultMerge.KeepNewerObservations([cached], [live, other]);

        Assert.Same(cached, Assert.Single(visible));
        Assert.Equal(UsageObservationOrigin.MemoryCache, visible[0].ObservationOrigin);
    }

    private static UsageResult Success(TestProvider provider, long sequence, double percent)
        => UsageResult.Success(provider.Id, provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(percent)), "live"))
            .AsLiveObservation(sequence, DateTimeOffset.Now);

    private static UsageResult LocalHistory(TestProvider provider)
        => UsageResult.Success(provider.Id, provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(0))
            {
                UsageHistory = new UsageHistory { Today = new UsagePeriod(10) },
            }, "Local usage history"));

    private sealed class TestProvider(ProviderId id) : IUsageProvider
    {
        public ProviderId Id => id;
        public string DisplayName => id.ToString();
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
