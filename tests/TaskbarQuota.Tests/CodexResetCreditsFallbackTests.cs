using System.Net;
using System.Text;
using System.Text.Json;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class CodexResetCreditsFallbackTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T09:00:00Z");

    private static ResetCreditsSnapshot Credits(int count = 2) => new(count,
    [
        new("available", Now.AddDays(-1), Now.AddDays(1)),
        new("available", Now.AddDays(-1), Now.AddDays(2)),
    ]);

    [Fact]
    public void FailureWithoutBaseline_DoesNotInventCredits()
        => Assert.Null(new CodexProvider().ResolveResetCredits(null, false, "account-a", Now));

    [Fact]
    public void FailureAfterSuccess_KeepsCountAndOriginalConfirmationTime()
    {
        var provider = new CodexProvider();
        var live = Credits();
        var confirmed = provider.ResolveResetCredits(live, true, "account-a", Now);
        Assert.Equal(live.AvailableCount, confirmed!.AvailableCount);
        Assert.Equal(Now, confirmed.ConfirmedAt);
        Assert.False(confirmed.IsCached);
        var cached = provider.ResolveResetCredits(null, false, "account-a", Now.AddMinutes(1));

        Assert.NotNull(cached);
        Assert.Equal(2, cached.AvailableCount);
        Assert.True(cached.IsCached);
        Assert.Equal(Now, cached.CachedAt);
        Assert.Null(live.CachedAt);
        Assert.Null(provider.ResolveResetCredits(null, false, "account-a", Now + ResetCreditsSnapshot.MaxCacheAge));
    }

    [Fact]
    public void SuccessfulZero_ReplacesOldCount()
    {
        var provider = new CodexProvider();
        provider.ResolveResetCredits(Credits(), true, "account-a", Now);
        var zero = new ResetCreditsSnapshot(0, []);
        var confirmedZero = provider.ResolveResetCredits(zero, true, "account-a", Now.AddMinutes(1));
        Assert.Equal(0, confirmedZero!.AvailableCount);
        Assert.Empty(confirmedZero.Credits);
        Assert.Equal(Now.AddMinutes(1), confirmedZero.ConfirmedAt);
        Assert.False(confirmedZero.IsCached);
        Assert.Equal(0, provider.ResolveResetCredits(null, false, "account-a", Now.AddMinutes(2))!.AvailableCount);
    }

    [Fact]
    public void SuccessfulMissingCreditBlock_ClearsOldCount()
    {
        var provider = new CodexProvider();
        provider.ResolveResetCredits(Credits(), true, "account-a", Now);
        Assert.Null(provider.ResolveResetCredits(null, true, "account-a", Now.AddMinutes(1)));
        Assert.Null(provider.ResolveResetCredits(null, false, "account-a", Now.AddMinutes(2)));
    }

    [Fact]
    public void IdentityChange_DiscardsFallbackEvenIfOriginalAccountReturns()
    {
        var provider = new CodexProvider();
        provider.ResolveResetCredits(Credits(), true, "account-a", Now);
        Assert.Null(provider.ResolveResetCredits(null, false, "account-b", Now.AddMinutes(1)));
        Assert.Null(provider.ResolveResetCredits(null, false, "account-a", Now.AddMinutes(2)));
    }

    [Fact]
    public void ExpiredCredits_AreRemovedFromFallback()
    {
        var provider = new CodexProvider();
        var credits = new ResetCreditsSnapshot(2,
        [
            new("available", Now.AddDays(-1), Now.AddMinutes(1)),
            new("available", Now.AddDays(-1), Now.AddDays(1)),
        ]);
        provider.ResolveResetCredits(credits, true, "account-a", Now);
        var cached = provider.ResolveResetCredits(null, false, "account-a", Now.AddMinutes(1));

        Assert.NotNull(cached);
        Assert.Equal(1, cached.AvailableCount);
        Assert.Single(cached.Credits);
        Assert.Equal(Now.AddDays(1), cached.EarliestExpiresAt);
    }

    [Fact]
    public void BackwardClock_DoesNotExtendFallback()
    {
        var provider = new CodexProvider();
        provider.ResolveResetCredits(Credits(), true, "account-a", Now);
        Assert.Null(provider.ResolveResetCredits(null, false, "account-a", Now.AddSeconds(-1)));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("network")]
    public async Task PartialEndpointFailure_KeepsCreditsWhileUsageRefreshes_AndRecovers(string failure)
    {
        using var handler = new ResetHandler();
        using var http = new HttpClient(handler);
        var provider = new CodexProvider(http);
        var creds = new CodexProvider.Credentials("test-token", "test-account");
        const string baseUrl = "https://example.test/backend-api";
        var first = await provider.FetchUsageAsync(creds, baseUrl);
        handler.Failure = failure;
        var failed = await provider.FetchUsageAsync(creds, baseUrl);
        handler.Failure = null;
        handler.AvailableCount = 1;
        var recovered = await provider.FetchUsageAsync(creds, baseUrl);

        Assert.Equal(10, first.Usage.Primary.UsedPercent);
        Assert.False(first.Usage.ResetCredits!.IsCached);
        Assert.Equal(20, failed.Usage.Primary.UsedPercent);
        Assert.Equal(2, failed.Usage.ResetCredits!.AvailableCount);
        Assert.True(failed.Usage.ResetCredits.IsCached);
        Assert.Equal(30, recovered.Usage.Primary.UsedPercent);
        Assert.Equal(1, recovered.Usage.ResetCredits!.AvailableCount);
        Assert.False(recovered.Usage.ResetCredits.IsCached);
        Assert.Equal(3, handler.ResetRequests);
    }

    [Theory]
    [InlineData("other-token", "test-account", "https://example.test/backend-api")]
    [InlineData("test-token", "other-account", "https://example.test/backend-api")]
    [InlineData("test-token", "test-account", "https://other.example.test/backend-api")]
    public async Task ChangedCredentialOrEndpoint_CannotReuseAnotherIdentity(string token, string account, string baseUrl)
    {
        using var handler = new ResetHandler();
        using var http = new HttpClient(handler);
        var provider = new CodexProvider(http);
        await provider.FetchUsageAsync(new("test-token", "test-account"), "https://example.test/backend-api");
        handler.Failure = "http";

        var result = await provider.FetchUsageAsync(new(token, account), baseUrl);
        Assert.Null(result.Usage.ResetCredits);
    }

    [Fact]
    public async Task OlderResponse_CannotResurrectCreditsAfterNewerZero()
    {
        using var handler = new OverlappingHandler();
        using var http = new HttpClient(handler);
        var provider = new CodexProvider(http);
        var creds = new CodexProvider.Credentials("test-token", "test-account");
        const string baseUrl = "https://example.test/backend-api";
        var olderRequest = provider.FetchUsageAsync(creds, baseUrl);
        ProviderFetchResult older;
        try
        {
            await handler.OlderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var newer = await provider.FetchUsageAsync(creds, baseUrl);
            Assert.Equal(0, newer.Usage.ResetCredits!.AvailableCount);
        }
        finally
        {
            handler.ReleaseOlder.TrySetResult();
            older = await olderRequest.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(2, older.Usage.ResetCredits!.AvailableCount);

        var failed = await provider.FetchUsageAsync(creds, baseUrl);
        Assert.Equal(0, failed.Usage.ResetCredits!.AvailableCount);
        Assert.True(failed.Usage.ResetCredits.IsCached);
    }

    [Fact]
    public async Task ServiceCache_DoesNotResurrectCreditsFromAnOlderCompletedRequest()
    {
        using var handler = new OverlappingHandler();
        using var http = new HttpClient(handler);
        var service = new UsageService();
        service.Register(new TransportProvider(new CodexProvider(http)));
        var olderRequest = service.FetchAsync(ProviderId.Codex, force: true);
        UsageResult newer;
        UsageResult older;
        try
        {
            await handler.OlderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            newer = await service.FetchAsync(ProviderId.Codex, force: true);
            Assert.Equal(0, newer.Fetch!.Usage.ResetCredits!.AvailableCount);
        }
        finally
        {
            handler.ReleaseOlder.TrySetResult();
            older = await olderRequest.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(newer.ObservationSequence, older.ObservationSequence);
        Assert.Equal(0, older.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.True(service.TryGetCached(ProviderId.Codex, out var cached));
        Assert.Equal(0, cached.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.Equal(newer.ObservationSequence, cached.ObservationSequence);
        Assert.True(service.TryGetLastSuccessfulLiveResult(ProviderId.Codex, out var lastGood));
        Assert.Equal(0, lastGood.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.Equal(newer.ObservationSequence, lastGood.ObservationSequence);
    }

    [Fact]
    public async Task CallerCancellation_IsPropagatedAndDoesNotReplaceBaseline()
    {
        using var handler = new ResetHandler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var provider = new CodexProvider(http);
        var creds = new CodexProvider.Credentials("test-token", "test-account");
        const string baseUrl = "https://example.test/backend-api";
        await provider.FetchUsageAsync(creds, baseUrl);
        var confirmedBefore = DateTimeOffset.UtcNow;
        handler.CancelOnReset = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.FetchUsageAsync(creds, baseUrl, cancellation.Token));
        handler.CancelOnReset = null;
        handler.Failure = "http";

        var failed = await provider.FetchUsageAsync(creds, baseUrl);
        Assert.Equal(2, failed.Usage.ResetCredits!.AvailableCount);
        Assert.True(failed.Usage.ResetCredits.CachedAt <= confirmedBefore);
    }

    [Theory]
    [InlineData(-11)]
    [InlineData(1)]
    public void ReusingResult_DropsExpiredOrFutureCachedCredits_WithoutMutatingOriginal(int minutes)
    {
        var usage = new UsageSnapshot(new RateWindow(34))
        {
            ResetCredits = new ResetCreditsSnapshot(2, [], DateTimeOffset.UtcNow.AddMinutes(minutes)),
        };
        var original = UsageResult.Success(ProviderId.Codex, new CodexProvider(), new ProviderFetchResult(usage, "test"));

        var fallback = original.AsFailureFallback(1, DateTimeOffset.Now);
        var memory = original.AsMemoryCache();
        Assert.Null(fallback.Fetch!.Usage.ResetCredits);
        Assert.Null(memory.Fetch!.Usage.ResetCredits);
        Assert.Equal(34, fallback.Fetch.Usage.Primary.UsedPercent);
        Assert.Equal(2, original.Fetch!.Usage.ResetCredits!.AvailableCount);
    }

    [Fact]
    public void ReusingResult_PrunesGrantsThatExpiredDuringMainEndpointFailure()
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new UsageSnapshot(new RateWindow(34))
        {
            ResetCredits = new ResetCreditsSnapshot(2,
            [
                new("available", now.AddDays(-1), now.AddSeconds(-1)),
                new("available", now.AddDays(-1), now.AddDays(1)),
            ], now.AddMinutes(-1)),
        };
        var original = UsageResult.Success(ProviderId.Codex, new CodexProvider(), new ProviderFetchResult(usage, "test"));
        var fallback = original.AsFailureFallback(1, now);
        Assert.Equal(1, fallback.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.Equal(2, original.Fetch!.Usage.ResetCredits!.AvailableCount);
    }

    [Theory]
    [InlineData(-11, false)]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public void FreshCredits_UseEndpointConfirmationWhenMainEndpointFails(int minutes, bool expectCredits)
    {
        var now = DateTimeOffset.UtcNow;
        var confirmation = now.AddMinutes(minutes);
        var credits = new ResetCreditsSnapshot(2,
        [
            new("available", now.AddDays(-1), now.AddSeconds(-1)),
            new("available", now.AddDays(-1), now.AddDays(1)),
        ], confirmedAt: confirmation);
        var original = UsageResult.Success(ProviderId.Codex, new CodexProvider(),
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(34)) { ResetCredits = credits }, "test"))
            .AsLiveObservation(1, now);

        var fallback = original.AsFailureFallback(2, now.AddMinutes(1));
        if (expectCredits)
        {
            Assert.Equal(1, fallback.Fetch!.Usage.ResetCredits!.AvailableCount);
            Assert.True(fallback.Fetch.Usage.ResetCredits.IsCached);
            Assert.Equal(confirmation, fallback.Fetch.Usage.ResetCredits.CachedAt);
        }
        else
            Assert.Null(fallback.Fetch!.Usage.ResetCredits);
        Assert.Equal(34, fallback.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(now, fallback.LastConfirmedAt);
        Assert.Equal(2, credits.AvailableCount);
        Assert.False(credits.IsCached);
    }

    [Fact]
    public async Task MainEndpointFailure_MarksPreviouslyLiveCreditsAsLastKnown()
    {
        using var handler = new ResetHandler { FixedUsage = true };
        using var http = new HttpClient(handler);
        var service = new UsageService();
        service.Register(new TransportProvider(new CodexProvider(http)));
        var live = await service.FetchAsync(ProviderId.Codex, force: true);
        handler.FailUsage = true;

        var failed = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.Equal(UsageObservationOrigin.FailureFallback, failed.ObservationOrigin);
        Assert.Equal(2, failed.Fetch!.Usage.ResetCredits!.AvailableCount);
        Assert.True(failed.Fetch.Usage.ResetCredits.IsCached);
        Assert.Equal(live.Fetch!.Usage.ResetCredits!.ConfirmedAt, failed.Fetch.Usage.ResetCredits.CachedAt);
        Assert.Equal(live.LastConfirmedAt, failed.LastConfirmedAt);
        Assert.False(live.Fetch.Usage.ResetCredits.IsCached);
    }

    [Fact]
    public async Task UnchangedQuotaAndCount_StillPublishesCachedAndRecoveredStates()
    {
        using var handler = new ResetHandler { FixedUsage = true };
        using var http = new HttpClient(handler);
        var provider = new TransportProvider(new CodexProvider(http));
        var service = new UsageService();
        service.Register(provider);
        var live = await service.FetchAsync(ProviderId.Codex, force: true);
        handler.Failure = "http";
        var cached = await service.FetchAsync(ProviderId.Codex, force: true);
        handler.Failure = null;
        var recovered = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.False(live.Fetch!.Usage.ResetCredits!.IsCached);
        Assert.True(cached.Fetch!.Usage.ResetCredits!.IsCached);
        Assert.False(recovered.Fetch!.Usage.ResetCredits!.IsCached);
        Assert.Equal(live.Fetch.Usage.Primary.UsedPercent, cached.Fetch.Usage.Primary.UsedPercent);
        Assert.Equal(2, recovered.Fetch.Usage.ResetCredits.AvailableCount);
    }

    [Fact]
    public async Task UnchangedQuota_RefreshesConfirmationWithoutChangingValueTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new ConfirmingProvider(now.AddMinutes(-5), now.AddMinutes(-1));
        var service = new UsageService();
        service.Register(provider);
        var first = await service.FetchAsync(ProviderId.Codex, force: true);
        var second = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.Equal(first.Fetch!.FetchedAt, second.Fetch!.FetchedAt);
        Assert.Equal(now.AddMinutes(-1), second.Fetch.Usage.ResetCredits!.ConfirmedAt);
        Assert.Equal(now.AddMinutes(-5), first.Fetch.Usage.ResetCredits!.ConfirmedAt);
        Assert.True(second.LastConfirmedAt >= first.LastConfirmedAt);
        Assert.Equal(second.LastConfirmedAt, second.AsFailureFallback(3, now.AddHours(1)).LastConfirmedAt);
    }

    private sealed class ConfirmingProvider(params DateTimeOffset[] confirmations) : IUsageProvider
    {
        private int _calls;
        public ProviderId Id => ProviderId.Codex;
        public string DisplayName => "Codex";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => Task.FromResult(new ProviderFetchResult(new UsageSnapshot(new RateWindow(34))
            {
                ResetCredits = new ResetCreditsSnapshot(2, [], confirmedAt: confirmations[_calls++]),
            }, "test"));
    }

    private sealed class TransportProvider(CodexProvider provider) : IUsageProvider
    {
        public ProviderId Id => ProviderId.Codex;
        public string DisplayName => "Codex";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => provider.FetchUsageAsync(new("test-token", "test-account"), "https://example.test/backend-api", ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedNewerMainRequestCannotResurrectCreditsOrUndoAnIdentitySwitch(bool switchAccount)
    {
        using var handler = new NewerMainFailureHandler();
        using var http = new HttpClient(handler);
        var provider = new CodexProvider(http);
        const string baseUrl = "https://example.test/backend-api";
        var firstIdentity = new CodexProvider.Credentials("token-a", "account-a");
        var nextIdentity = switchAccount ? new CodexProvider.Credentials("token-b", "account-b") : firstIdentity;
        await provider.FetchUsageAsync(firstIdentity, baseUrl);
        var older = provider.FetchUsageAsync(firstIdentity, baseUrl);
        try
        {
            await handler.ResetStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<ProviderException>(() => provider.FetchUsageAsync(nextIdentity, baseUrl));
        }
        finally { handler.ReleaseReset.TrySetResult(); }
        Assert.Equal(0, (await older.WaitAsync(TimeSpan.FromSeconds(10))).Usage.ResetCredits!.AvailableCount);
        var fallback = await provider.FetchUsageAsync(nextIdentity, baseUrl);
        if (switchAccount) Assert.Null(fallback.Usage.ResetCredits);
        else
        {
            Assert.Equal(0, fallback.Usage.ResetCredits!.AvailableCount);
            Assert.True(fallback.Usage.ResetCredits.IsCached);
        }
    }

    private sealed class NewerMainFailureHandler : HttpMessageHandler
    {
        public TaskCompletionSource ResetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReset { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _mainRequests;
        private int _resetRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal))
                return Interlocked.Increment(ref _mainRequests) == 3
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json("""{"rate_limit":{"primary_window":{"used_percent":34}}}""");
            switch (Interlocked.Increment(ref _resetRequests))
            {
                case 1: return Json("""{"available_count":2,"credits":[]}""");
                case 2:
                    ResetStarted.TrySetResult();
                    await ReleaseReset.Task.WaitAsync(ct);
                    return Json("""{"available_count":0,"credits":[]}""");
                default: return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
        }
    }

    private sealed class OverlappingHandler : HttpMessageHandler
    {
        public TaskCompletionSource OlderStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOlder { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _resetRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal))
                return Json("""{"rate_limit":{"primary_window":{"used_percent":34}}} """);
            switch (Interlocked.Increment(ref _resetRequests))
            {
                case 1:
                    OlderStarted.TrySetResult();
                    await ReleaseOlder.Task.WaitAsync(ct);
                    return Json("""{"available_count":2,"credits":[]} """);
                case 2:
                    return Json("""{"available_count":0,"credits":[]} """);
                default:
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class ResetHandler : HttpMessageHandler
    {
        public string? Failure { get; set; }
        public int AvailableCount { get; set; } = 2;
        public int ResetRequests { get; private set; }
        public bool FixedUsage { get; init; }
        public bool FailUsage { get; set; }
        public CancellationTokenSource? CancelOnReset { get; set; }
        private int _usageRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal))
            {
                if (FailUsage)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                _usageRequests++;
                return Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    rate_limit = new
                    {
                        primary_window = new
                        {
                            used_percent = FixedUsage ? 10 : _usageRequests * 10,
                            limit_window_seconds = 604800,
                        },
                    },
                })));
            }
            Assert.EndsWith("/rate-limit-reset-credits", request.RequestUri.AbsolutePath);
            ResetRequests++;
            if (CancelOnReset is { } cancellation)
            {
                cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(ct);
            }
            return Failure switch
            {
                "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException()),
                "network" => Task.FromException<HttpResponseMessage>(new HttpRequestException()),
                "malformed" => Task.FromResult(Json("{broken")),
                _ => Task.FromResult(Json($$"""{"available_count":{{AvailableCount}},"credits":[]} """)),
            };
        }

    }
}
