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
        Assert.Same(live, provider.ResolveResetCredits(live, true, "account-a", Now));
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
        Assert.Same(zero, provider.ResolveResetCredits(zero, true, "account-a", Now.AddMinutes(1)));
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
        await handler.OlderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var newer = await provider.FetchUsageAsync(creds, baseUrl);
            Assert.Equal(0, newer.Usage.ResetCredits!.AvailableCount);
        }
        finally
        {
            handler.ReleaseOlder.TrySetResult();
        }
        Assert.Equal(2, (await olderRequest).Usage.ResetCredits!.AvailableCount);

        var failed = await provider.FetchUsageAsync(creds, baseUrl);
        Assert.Equal(0, failed.Usage.ResetCredits!.AvailableCount);
        Assert.True(failed.Usage.ResetCredits.IsCached);
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
        public CancellationTokenSource? CancelOnReset { get; set; }
        private int _usageRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal))
            {
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
