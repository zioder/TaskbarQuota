using TaskbarQuota.Controls;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

[Collection(WidgetRowSettingsCollection.Name)]
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
        var cachedFallback = await service.FetchAsync(ProviderId.Claude);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Same(first.Fetch, second.Fetch);
        Assert.Equal(UsageObservationOrigin.Live, first.ObservationOrigin);
        Assert.Equal(UsageObservationOrigin.FailureFallback, second.ObservationOrigin);
        Assert.Equal(UsageObservationOrigin.FailureFallback, cachedFallback.ObservationOrigin);
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

    [Theory]
    [InlineData(null)]
    [InlineData(ProviderErrorKind.Other)]
    [InlineData(ProviderErrorKind.RateLimited)]
    public async Task FetchAsync_CodexNetworkFailureKeepsLastKnownQuotaUntilReset(ProviderErrorKind? errorKind)
    {
        var service = new UsageService();
        var provider = new FlakyProvider
        {
            Id = ProviderId.Codex,
            NextResetAt = DateTimeOffset.Now.AddHours(2),
        };
        service.Register(provider);

        var live = await service.FetchAsync(provider.Id, force: true);
        provider.NextException = errorKind is { } kind
            ? new ProviderException(kind, "Synthetic API failure")
            : new HttpRequestException("Synthetic network failure");

        var fallback = await service.FetchAsync(provider.Id, force: true);

        Assert.Same(live.Fetch, fallback.Fetch);
        Assert.True(fallback.IsStale);
        Assert.Equal(UsageObservationOrigin.FailureFallback, fallback.ObservationOrigin);
        Assert.Equal(live.LastConfirmedAt, fallback.LastConfirmedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ProviderErrorKind.Other)]
    [InlineData(ProviderErrorKind.RateLimited)]
    public async Task FetchAsync_CodexNetworkFailureDoesNotKeepAQuotaPastReset(ProviderErrorKind? errorKind)
    {
        var service = new UsageService();
        var provider = new FlakyProvider
        {
            Id = ProviderId.Codex,
            NextResetAt = DateTimeOffset.Now.AddMinutes(-1),
        };
        service.Register(provider);
        Assert.True((await service.FetchAsync(provider.Id, force: true)).Ok);
        Assert.True(service.TryGetCached(provider.Id, out _));
        Assert.False(service.TryGetLastSuccessfulLiveResult(provider.Id, out _));
        provider.NextException = errorKind is { } kind
            ? new ProviderException(kind, "Synthetic API failure")
            : new HttpRequestException("Synthetic network failure");

        Assert.False((await service.FetchAsync(provider.Id, force: true)).Ok);
    }

    [Fact]
    public void CodexFallbackExpiresAfterTheLastConfirmationAgesOut()
    {
        var now = DateTimeOffset.UtcNow;
        var confirmedAt = now - UsageSnapshotStore.MaxRestoreAge;
        var provider = new FlakyProvider { Id = ProviderId.Codex };
        var result = UsageResult.Success(provider.Id, provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(42)), "test"), confirmedAt)
            .AsFailureFallback(1, now, isStale: true);

        Assert.True(UsageService.CanReuseQuota(result, now));
        Assert.False(UsageService.CanReuseQuota(result, now.AddMinutes(1)));
    }

    [Fact]
    public async Task FetchAsync_CodexLocalHistoryDoesNotRenderAnInventedQuota()
    {
        var path = Path.Combine(TestEnvironment.AppDataDirectory, $"quota-history-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(path, "{\"timestamp\":\"" + DateTimeOffset.UtcNow.ToString("O") + """
            ","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"total_tokens":100}}}}
            """);
        var previousDiscovery = UsageHistoryService.DiscoverFilesOverrideForTesting;
        try
        {
            UsageHistoryService.DiscoverFilesOverrideForTesting = id => id == ProviderId.Codex ? [path] : [];
            var service = new UsageService();
            var provider = new FlakyProvider
            {
                Id = ProviderId.Codex,
                NextException = new HttpRequestException("Synthetic network failure"),
            };
            service.Register(provider);

            var fallback = await service.FetchAsync(provider.Id, force: true);

            Assert.Equal(UsageObservationOrigin.FailureFallback, fallback.ObservationOrigin);
            Assert.NotNull(fallback.Fetch);
            Assert.NotNull(fallback.Fetch.Usage.UsageHistory);
            Assert.False(fallback.Fetch.Usage.HasPrimaryWindow);
            Assert.Empty(WidgetSummary.BuildRowLabelsForTesting(fallback, fallback.Fetch.Usage));
        }
        finally
        {
            UsageHistoryService.DiscoverFilesOverrideForTesting = previousDiscovery;
            File.Delete(path);
        }
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

        Assert.Same(first.Fetch, second.Fetch);
        Assert.Equal(UsageObservationOrigin.Live, first.ObservationOrigin);
        Assert.Equal(UsageObservationOrigin.Live, second.ObservationOrigin);
        Assert.True(second.ObservationSequence > first.ObservationSequence);
        Assert.Equal(2, provider.FetchCount);
    }

    [Fact]
    public async Task FetchAsync_CachedSuccessIsMarkedAsMemoryCache()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var live = await service.FetchAsync(ProviderId.Claude, force: true);
        var cached = await service.FetchAsync(ProviderId.Claude);

        Assert.Equal(UsageObservationOrigin.Live, live.ObservationOrigin);
        Assert.Equal(UsageObservationOrigin.MemoryCache, cached.ObservationOrigin);
        Assert.Equal(live.ObservationSequence, cached.ObservationSequence);
        Assert.Same(live.Fetch, cached.Fetch);
        Assert.Equal(1, provider.FetchCount);
    }

    private sealed class FlakyProvider : IUsageProvider
    {
        public Exception? NextException { get; set; }
        public double? NextPrimaryPercent { get; set; }
        public double? NextSecondaryPercent { get; set; }
        public DateTimeOffset? NextResetAt { get; set; }
        public int FetchCount { get; private set; }

        public ProviderId Id { get; init; } = ProviderId.Claude;
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
