using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Tests;

public sealed class UsageCacheIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TaskbarQuota-identity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("token")]
    [InlineData("endpoint")]
    public async Task ChangedIdentity_CannotReadMemoryOrReuseItAfterNetworkFailure(string change)
    {
        using var context = new Context();
        var service = new UsageService();
        service.Register(context.Provider);
        var live = await service.FetchAsync(ProviderId.Codex, force: true);
        Assert.True(live.Ok);
        Assert.Matches("^[0-9A-F]{64}$", live.Fetch!.CacheIdentity!);

        context.Change(change);
        Assert.False(service.TryGetCached(ProviderId.Codex, out _));
        Assert.False(service.TryGetLastSuccessfulLiveResult(ProviderId.Codex, out _));
        context.Handler.MainStatus = HttpStatusCode.ServiceUnavailable;
        var failed = await service.FetchAsync(ProviderId.Codex);
        Assert.False(failed.Ok);
        Assert.Equal(ProviderErrorKind.Other, failed.ErrorKind);
        Assert.Contains("503", failed.Error);
        Assert.Equal(2, context.Handler.MainRequests);
    }

    [Fact]
    public async Task CapturedRequestFromPreviousIdentity_CannotBecomeCurrentWithoutAnotherFetch()
    {
        using var context = new Context();
        context.Handler.BlockMain = true;
        var service = new UsageService();
        service.Register(context.Provider);
        var request = service.FetchAsync(ProviderId.Codex, force: true);
        UsageResult result;
        try
        {
            await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            context.Change("account");
        }
        finally
        {
            context.Handler.Release.TrySetResult();
            result = await request.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.Other, result.ErrorKind);
        Assert.Contains("changed", result.Error);
        Assert.False(service.TryGetCached(ProviderId.Codex, out _));
        Assert.False(service.TryGetLastSuccessfulLiveResult(ProviderId.Codex, out _));
    }

    [Fact]
    public async Task MissingCredentials_DoNotExposePreviousAccountOrInventAnotherError()
    {
        using var context = new Context();
        var service = new UsageService();
        service.Register(context.Provider);
        Assert.True((await service.FetchAsync(ProviderId.Codex, force: true)).Ok);
        context.CredentialsUnavailable = true;

        Assert.False(service.TryGetCached(ProviderId.Codex, out _));
        Assert.False(service.TryGetLastSuccessfulLiveResult(ProviderId.Codex, out _));
        var failed = await service.FetchAsync(ProviderId.Codex);
        Assert.False(failed.Ok);
        Assert.Equal(ProviderErrorKind.AuthRequired, failed.ErrorKind);
        Assert.Contains("test credentials unavailable", failed.Error);
    }

    [Fact]
    public async Task PreviousAccountsAuthenticationError_IsNotAttributedToCurrentAccount()
    {
        using var context = new Context();
        context.Handler.BlockMain = true;
        context.Handler.MainStatus = HttpStatusCode.Unauthorized;
        var service = new UsageService();
        service.Register(context.Provider);
        var request = service.FetchAsync(ProviderId.Codex, force: true);
        UsageResult result;
        try
        {
            await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            context.Change("account");
        }
        finally
        {
            context.Handler.Release.TrySetResult();
            result = await request.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False(result.Ok);
        Assert.Equal(ProviderErrorKind.Other, result.ErrorKind);
        Assert.Contains("changed", result.Error);
        Assert.False(service.TryGetCached(ProviderId.Codex, out _));
    }

    [Fact]
    public async Task CachedAuthenticationFailure_DoesNotPreventNewAccountFromFetching()
    {
        using var context = new Context();
        context.Handler.MainStatus = HttpStatusCode.Unauthorized;
        var service = new UsageService();
        service.Register(context.Provider);
        var failed = await service.FetchAsync(ProviderId.Codex);
        Assert.Equal(ProviderErrorKind.AuthRequired, failed.ErrorKind);
        Assert.True(service.TryGetCached(ProviderId.Codex, out var cached));
        Assert.Equal(failed.CacheIdentity, cached.CacheIdentity);
        Assert.Equal(1, context.Handler.MainRequests);
        Assert.False((await service.FetchAsync(ProviderId.Codex)).Ok);
        Assert.Equal(1, context.Handler.MainRequests);
        context.Change("account");
        context.Handler.MainStatus = HttpStatusCode.OK;

        Assert.False(service.TryGetCached(ProviderId.Codex, out _));
        var next = await service.FetchAsync(ProviderId.Codex);
        Assert.True(next.Ok);
        Assert.Equal(2, context.Handler.MainRequests);
        Assert.NotEqual(failed.CacheIdentity, next.CacheIdentity);
    }

    [Fact]
    public async Task SameIdentity_KeepsFallbackAndOriginalConfirmationWhenMainEndpointFails()
    {
        using var context = new Context();
        var service = new UsageService();
        service.Register(context.Provider);
        var first = await service.FetchAsync(ProviderId.Codex, force: true);
        context.Handler.MainStatus = HttpStatusCode.ServiceUnavailable;
        var fallback = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.True(fallback.Ok);
        Assert.Equal(first.CacheIdentity, fallback.CacheIdentity);
        Assert.Equal(first.LastConfirmedAt, fallback.LastConfirmedAt);
        Assert.Equal(34, fallback.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(2, fallback.Fetch.Usage.ResetCredits!.AvailableCount);
        Assert.True(fallback.Fetch.Usage.ResetCredits.IsCached);
    }

    [Fact]
    public async Task NewerFailureFromAnotherAccount_CannotReplaceCurrentAccountInFlightResult()
    {
        using var context = new Context();
        context.Handler.BlockMain = true;
        var service = new UsageService();
        service.Register(context.Provider);
        var original = service.FetchAsync(ProviderId.Codex, force: true);
        UsageResult otherFailure;
        UsageResult current;
        try
        {
            await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            context.Change("account");
            context.Handler.MainStatus = HttpStatusCode.Unauthorized;
            otherFailure = await service.FetchAsync(ProviderId.Codex, force: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ProviderErrorKind.AuthRequired, otherFailure.ErrorKind);
            Assert.True(service.TryGetCached(ProviderId.Codex, out _));
            context.Change("original");
            context.Handler.MainStatus = HttpStatusCode.OK;
        }
        finally
        {
            context.Handler.Release.TrySetResult();
            current = await original.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(current.Ok);
        Assert.Equal(34, current.Fetch!.Usage.Primary.UsedPercent);
        Assert.True(UsageCacheIdentity.IsCurrent(context.Provider, current.Fetch, current.CacheIdentity));
        Assert.True(current.ObservationSequence < otherFailure.ObservationSequence);
        Assert.True(current.PublicationOrder > otherFailure.PublicationOrder);
        Assert.True(service.TryGetCached(ProviderId.Codex, out var cached));
        Assert.Equal(current.ObservationSequence, cached.ObservationSequence);
        var gate = new UsageObservationSequenceGate();
        Assert.True(gate.TryAccept(otherFailure));
        Assert.True(gate.TryAccept(current));
        Assert.Same(current, DashboardResultMerge.KeepNewerObservations([current], [otherFailure])[0]);
        var refreshed = await service.FetchAsync(ProviderId.Codex).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(refreshed.Ok);
        Assert.Equal(current.CacheIdentity, refreshed.CacheIdentity);
    }

    [Fact]
    public async Task DiscoveredAccountChange_ClearsANewerOldAccountValueAlreadyShownByConsumers()
    {
        using var context = new Context();
        context.Handler.BlockMain = true;
        var service = new UsageService();
        service.Register(context.Provider);
        var olderCall = service.FetchAsync(ProviderId.Codex, force: true);
        UsageResult newer;
        UsageResult changed;
        try
        {
            await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            newer = await service.FetchAsync(ProviderId.Codex, force: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(newer.Ok);
            context.Change("account");
        }
        finally
        {
            context.Handler.Release.TrySetResult();
            changed = await olderCall.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(changed.Ok);
        Assert.Equal(ProviderErrorKind.Other, changed.ErrorKind);
        Assert.True(changed.ObservationSequence < newer.ObservationSequence);
        Assert.True(changed.PublicationOrder > newer.PublicationOrder);
        var gate = new UsageObservationSequenceGate();
        Assert.True(gate.TryAccept(newer));
        Assert.True(gate.TryAccept(changed));
        Assert.Same(changed, DashboardResultMerge.KeepNewerObservations([changed], [newer])[0]);
        Assert.False(service.TryGetCached(ProviderId.Codex, out _));
    }

    [Fact]
    public async Task ReissuedFailureFallback_DoesNotReplaceTheLastSuccessfulLiveObservation()
    {
        using var context = new Context();
        var service = new UsageService();
        service.Register(context.Provider);
        var baseline = await service.FetchAsync(ProviderId.Codex, force: true);
        context.Handler.BlockMain = true;
        context.Handler.BlockOnRequest = 2;
        var olderCall = service.FetchAsync(ProviderId.Codex, force: true);
        UsageResult otherFailure;
        UsageResult fallback;
        try
        {
            await context.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            context.Change("account");
            context.Handler.MainStatus = HttpStatusCode.Unauthorized;
            otherFailure = await service.FetchAsync(ProviderId.Codex, force: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(otherFailure.Ok);
            context.Change("original");
            context.Handler.MainStatus = HttpStatusCode.ServiceUnavailable;
        }
        finally
        {
            context.Handler.Release.TrySetResult();
            fallback = await olderCall.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(fallback.Ok);
        Assert.Equal(UsageObservationOrigin.FailureFallback, fallback.ObservationOrigin);
        Assert.True(fallback.ObservationSequence < otherFailure.ObservationSequence);
        Assert.True(fallback.PublicationOrder > otherFailure.PublicationOrder);
        Assert.Equal(baseline.LastConfirmedAt, fallback.LastConfirmedAt);
        Assert.True(service.TryGetLastSuccessfulLiveResult(ProviderId.Codex, out var lastGood));
        Assert.Equal(baseline.ObservationSequence, lastGood.ObservationSequence);
        Assert.Equal(baseline.LastConfirmedAt, lastGood.LastConfirmedAt);
    }

    [Fact]
    public void ReissuingAnObservation_DoesNotRenewAnyConfirmationTimestamp()
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var provider = new Context();
        using (provider)
        {
            var result = UsageResult.Success(ProviderId.Codex, provider.Provider,
                new ProviderFetchResult(new UsageSnapshot(new RateWindow(34))
                {
                    ResetCredits = new ResetCreditsSnapshot(2, [], confirmedAt: at),
                }, "test", at.AddHours(-1)), at).AsLiveObservation(1, at);
            var reissued = result.WithPublicationOrder(new UsagePublicationOrder(3, 1));
            Assert.Equal(result.ObservationSequence, reissued.ObservationSequence);
            Assert.Equal(new UsagePublicationOrder(3, 1), reissued.PublicationOrder);
            Assert.Equal(result.ObservedAt, reissued.ObservedAt);
            Assert.Equal(result.LastConfirmedAt, reissued.LastConfirmedAt);
            Assert.Equal(result.Fetch!.FetchedAt, reissued.Fetch!.FetchedAt);
            Assert.Equal(at, reissued.Fetch.Usage.ResetCredits!.ConfirmedAt);
            Assert.False(reissued.Fetch.Usage.ResetCredits.IsCached);
        }
    }

    [Fact]
    public async Task FailedAttemptByAnotherIdentity_ClearsInternalResetCreditBaseline()
    {
        using var context = new Context();
        Assert.Equal(2, (await context.Provider.FetchUsageAsync()).Usage.ResetCredits!.AvailableCount);
        context.Change("account");
        context.Handler.MainStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<ProviderException>(() => context.Provider.FetchUsageAsync());
        context.Change("original");
        context.Handler.MainStatus = HttpStatusCode.OK;
        context.Handler.FailReset = true;

        Assert.Null((await context.Provider.FetchUsageAsync()).Usage.ResetCredits);
    }

    [Fact]
    public async Task SnapshotHash_SurvivesClonesButOldOrLegacyIdentityCannotRestore()
    {
        using var context = new Context();
        var fetch = await context.Provider.FetchUsageAsync();
        var live = UsageResult.Success(ProviderId.Codex, context.Provider, fetch).AsLiveObservation(1, DateTimeOffset.Now);
        var fallback = live.AsFailureFallback(2, DateTimeOffset.Now);
        Assert.Equal(fetch.CacheIdentity, fallback.Fetch!.CacheIdentity);
        Assert.Equal(fetch.CacheIdentity, fallback.WithSource(null).CacheIdentity);
        Assert.True(fallback.Fetch.Usage.ResetCredits!.IsCached);
        UsageSnapshotStore.Save(_directory, new Dictionary<ProviderId, UsageResult> { [ProviderId.Codex] = live });
        var path = Path.Combine(_directory, "usage-snapshots.json");
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("secret-token-a", json);
        Assert.DoesNotContain("private-account-a", json);
        Assert.DoesNotContain("example.test", json);
        var restored = UsageSnapshotStore.Load(_directory, _ => context.Provider)[ProviderId.Codex];
        Assert.Equal(fetch.CacheIdentity, restored.Fetch!.CacheIdentity);
        Assert.Equal(fetch.CacheIdentity, restored.CacheIdentity);

        context.Change("account");
        Assert.Empty(UsageSnapshotStore.Load(_directory, _ => context.Provider));
        // A delayed save from the previous account also can't write its quota into the current cache.
        UsageSnapshotStore.Save(_directory, new Dictionary<ProviderId, UsageResult> { [ProviderId.Codex] = live });
        Assert.Empty(JsonNode.Parse(File.ReadAllText(path))!["Entries"]!.AsArray());

        context.Change("original");
        UsageSnapshotStore.Save(_directory, new Dictionary<ProviderId, UsageResult> { [ProviderId.Codex] = live });
        var legacy = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var entry in legacy["Entries"]!.AsArray()) entry!.AsObject().Remove("CacheIdentity");
        File.WriteAllText(path, legacy.ToJsonString());
        Assert.Empty(UsageSnapshotStore.Load(_directory, _ => context.Provider));
    }

    private sealed class Context : IDisposable
    {
        private CodexProvider.Credentials _credentials = new("secret-token-a", "private-account-a");
        private string _endpoint = "https://example.test/backend-api";
        private readonly HttpClient _http;
        internal Handler Handler { get; } = new();
        internal CodexProvider Provider { get; }
        internal bool CredentialsUnavailable { get; set; }

        internal Context()
        {
            _http = new HttpClient(Handler);
            Provider = new CodexProvider(_http,
                () => CredentialsUnavailable ? throw new ProviderException(ProviderErrorKind.AuthRequired, "test credentials unavailable") : _credentials,
                () => _endpoint);
        }

        internal void Change(string change)
        {
            _credentials = change switch
            {
                "account" => new("secret-token-a", "private-account-b"),
                "token" => new("secret-token-b", "private-account-a"),
                _ => new("secret-token-a", "private-account-a"),
            };
            _endpoint = change == "endpoint" ? "https://other.example.test/backend-api" : "https://example.test/backend-api";
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal HttpStatusCode MainStatus { get; set; } = HttpStatusCode.OK;
        internal bool BlockMain { get; set; }
        internal int BlockOnRequest { get; set; } = 1;
        internal bool FailReset { get; set; }
        internal int MainRequests { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal))
            {
                MainRequests++;
                if (BlockMain && MainRequests == BlockOnRequest)
                {
                    Started.TrySetResult();
                    await Release.Task.WaitAsync(ct);
                }
                if (MainStatus != HttpStatusCode.OK) return new HttpResponseMessage(MainStatus);
                return Json("""{"rate_limit":{"primary_window":{"used_percent":34}}}""");
            }
            return FailReset ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json("""{"available_count":2,"credits":[]}""");
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };
    }
}
