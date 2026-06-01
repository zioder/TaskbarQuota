using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class FetchCachePolicyTests
{
    [Fact]
    public void TtlForFailure_RateLimited_UsesFiveMinuteBackoff()
        => Assert.Equal(UsageService.RateLimitedCacheTtl, FetchCachePolicy.TtlForFailure(ProviderErrorKind.RateLimited));

    [Fact]
    public void TtlForFailure_OtherErrors_UsesShortBackoff()
        => Assert.Equal(UsageService.FailureCacheTtl, FetchCachePolicy.TtlForFailure(ProviderErrorKind.Other));

    [Fact]
    public void TtlForSuccess_UsesSixtySeconds()
        => Assert.Equal(UsageService.SuccessCacheTtl, FetchCachePolicy.TtlForSuccess());

    [Fact]
    public async Task FetchAsync_RateLimitedAfterSuccess_ReturnsLastSuccessfulLiveResult()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Claude, force: true);
        provider.NextException = new ProviderException(ProviderErrorKind.RateLimited, "429");

        var second = await service.FetchAsync(ProviderId.Claude, force: true);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Same(first.Fetch, second.Fetch);
        Assert.Equal(42, second.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(73, second.Fetch.Usage.Secondary!.UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_TransientFailureAfterSuccess_DoesNotPublishZeroUsage()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Claude, force: true);
        provider.NextException = new ProviderException(ProviderErrorKind.Other, "Claude API returned 500");

        var second = await service.FetchAsync(ProviderId.Claude, force: true);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(42, second.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(73, second.Fetch.Usage.Secondary!.UsedPercent);
        Assert.NotEqual(0, second.Fetch.Usage.Primary.UsedPercent);
        Assert.NotEqual(0, second.Fetch.Usage.Secondary.UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_ClaudeZeroSnapshotAfterSuccess_DoesNotPublishZeroUsage()
    {
        var service = new UsageService();
        var provider = new FlakyProvider
        {
            NextResetAt = DateTimeOffset.Now.AddHours(4),
        };
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Claude, force: true);
        provider.NextPrimaryPercent = 0;
        provider.NextSecondaryPercent = 0;
        provider.NextResetAt = first.Fetch!.Usage.Primary.ResetAt;

        var second = await service.FetchAsync(ProviderId.Claude, force: true);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(42, second.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(73, second.Fetch.Usage.Secondary!.UsedPercent);
        Assert.NotEqual(0, second.Fetch.Usage.Primary.UsedPercent);
        Assert.NotEqual(0, second.Fetch.Usage.Secondary.UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_ClaudeZeroSnapshotWithoutSuccess_IsAllowed()
    {
        var service = new UsageService();
        var provider = new FlakyProvider
        {
            NextPrimaryPercent = 0,
            NextSecondaryPercent = 0,
        };
        service.Register(provider);

        var result = await service.FetchAsync(ProviderId.Claude, force: true);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(0, result.Fetch.Usage.Secondary!.UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_ClaudeZeroSnapshotAfterResetAdvanced_IsAllowed()
    {
        var service = new UsageService();
        var provider = new FlakyProvider
        {
            NextResetAt = DateTimeOffset.Now.AddMinutes(5),
        };
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Claude, force: true);
        provider.NextPrimaryPercent = 0;
        provider.NextSecondaryPercent = 0;
        provider.NextResetAt = first.Fetch!.Usage.Primary.ResetAt!.Value.AddHours(5);

        var second = await service.FetchAsync(ProviderId.Claude, force: true);

        Assert.True(second.Ok);
        Assert.Equal(0, second.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(0, second.Fetch.Usage.Secondary!.UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_AuthFailureAfterSuccess_ReturnsFailureInsteadOfStaleUsage()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Claude, force: true);
        provider.NextException = new ProviderException(ProviderErrorKind.AuthRequired, "Claude OAuth token expired.");

        var second = await service.FetchAsync(ProviderId.Claude, force: true);

        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Contains("expired", second.Error);
    }

    [Fact]
    public async Task FetchAsync_SameLiveUsage_ReturnsPreviousSuccessfulResult()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Claude, force: true);
        var second = await service.FetchAsync(ProviderId.Claude, force: true);

        Assert.Same(first, second);
        Assert.Equal(2, provider.FetchCount);
    }

    private sealed class FlakyProvider : IUsageProvider
    {
        public ProviderException? NextException { get; set; }
        public double? NextPrimaryPercent { get; set; }
        public double? NextSecondaryPercent { get; set; }
        public DateTimeOffset? NextResetAt { get; set; }
        public int FetchCount { get; private set; }

        public ProviderId Id => ProviderId.Claude;
        public string DisplayName => "Claude Code";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            FetchCount++;
            if (NextException is { } exception)
            {
                NextException = null;
                throw exception;
            }

            var usage = new UsageSnapshot(new RateWindow(NextPrimaryPercent ?? 42, resetAt: NextResetAt))
            {
                Secondary = new RateWindow(NextSecondaryPercent ?? 73, resetAt: NextResetAt),
                LoginMethod = "Max",
            };
            NextPrimaryPercent = null;
            NextSecondaryPercent = null;
            NextResetAt = null;
            return Task.FromResult(new ProviderFetchResult(usage, "live"));
        }
    }
}
