using System.Threading;
using System.Threading.Tasks;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class UsageCoordinatorOrderingTests
{
    [Fact]
    public void SortByRecentActivity_PutsActiveFirstAndPreviousBelow()
    {
        var results = Results(ProviderId.Claude, ProviderId.Codex, ProviderId.Cursor);
        var ordered = UsageCoordinator.SortByRecentActivity(
            results,
            new[] { ProviderId.Codex, ProviderId.Claude },
            ProviderId.Codex);

        Assert.Equal(
            new[] { ProviderId.Codex, ProviderId.Claude, ProviderId.Cursor },
            ordered.Select(r => r.Id));
    }

    [Fact]
    public void SortByRecentActivity_WhenPreviousProviderBecomesActive_MovesItBackToTop()
    {
        var results = Results(ProviderId.Claude, ProviderId.Codex, ProviderId.Cursor);
        var ordered = UsageCoordinator.SortByRecentActivity(
            results,
            new[] { ProviderId.Claude, ProviderId.Codex },
            ProviderId.Claude);

        Assert.Equal(
            new[] { ProviderId.Claude, ProviderId.Codex, ProviderId.Cursor },
            ordered.Select(r => r.Id));
    }

    [Fact]
    public void SortByRecentActivity_KeepsUnknownProvidersInOriginalOrder()
    {
        var results = Results(ProviderId.Cursor, ProviderId.Antigravity, ProviderId.OpenCode);
        var ordered = UsageCoordinator.SortByRecentActivity(
            results,
            new[] { ProviderId.Claude, ProviderId.Codex },
            ProviderId.Claude);

        Assert.Equal(
            new[] { ProviderId.Cursor, ProviderId.Antigravity, ProviderId.OpenCode },
            ordered.Select(r => r.Id));
    }

    [Theory]
    [InlineData(ProviderId.OpenCode, true)]
    [InlineData(ProviderId.OpenCodeGo, true)]
    [InlineData(ProviderId.Cursor, false)]
    [InlineData(ProviderId.Codex, false)]
    public void ShouldReactToOpenCodeModelChange_OnlyWhenOpenCodeIsForeground(ProviderId foreground, bool expected)
        => Assert.Equal(expected, UsageCoordinator.ShouldReactToOpenCodeModelChange(foreground));

    [Fact]
    public void ShouldReactToOpenCodeModelChange_ReturnsFalseWhenNothingDetected()
        => Assert.False(UsageCoordinator.ShouldReactToOpenCodeModelChange(null));

    private static IReadOnlyList<UsageResult> Results(params ProviderId[] ids)
        => ids.Select(id => UsageResult.Pending(id, new TestProvider(id), "Loading")).ToArray();

    private sealed class TestProvider(ProviderId id) : IUsageProvider
    {
        public ProviderId Id { get; } = id;
        public string DisplayName => Id.ToString();
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => Task.FromResult(new ProviderFetchResult(new UsageSnapshot(new RateWindow(0)), "test"));
    }
}
