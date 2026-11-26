using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Tests;

public class UsagePublicationOrderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReissuedOldRequestCannotSupersedeANewerPendingRequest(bool newerStartedBeforeForeignAccount)
    {
        var first = new PlannedResult(10, blocked: true);
        var next = new PlannedResult(75, blocked: true);
        var foreign = new PlannedResult(error: ProviderErrorKind.AuthRequired);
        var provider = new OrderedProvider(newerStartedBeforeForeignAccount ? [first, next, foreign] : [first, foreign, next]);
        var service = Service(provider);
        var oldCall = service.FetchAsync(provider.Id, force: true);
        Task<UsageResult>? nextCall = null;
        UsageResult old;
        UsageResult newest;
        UsageResult wrongAccount;
        try
        {
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (newerStartedBeforeForeignAccount)
            {
                nextCall = service.FetchAsync(provider.Id, force: true);
                await next.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            provider.Identity = "A";
            wrongAccount = await service.FetchAsync(provider.Id, force: true).WaitAsync(TimeSpan.FromSeconds(10));
            provider.Identity = "B";
            if (nextCall is null)
            {
                nextCall = service.FetchAsync(provider.Id, force: true);
                await next.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            first.Release.TrySetResult();
            old = await oldCall.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, old.ObservationSequence);
            Assert.True(old.PublicationOrder > wrongAccount.PublicationOrder);
            next.Release.TrySetResult();
            newest = await nextCall.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            first.Release.TrySetResult();
            next.Release.TrySetResult();
            await oldCall.WaitAsync(TimeSpan.FromSeconds(10));
            if (nextCall is not null) await nextCall.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(newerStartedBeforeForeignAccount ? 2 : 3, newest.ObservationSequence);
        Assert.Equal(75, newest.Fetch!.Usage.Primary.UsedPercent);
        Assert.True(newest.PublicationOrder > old.PublicationOrder);
        Assert.True(service.TryGetCached(provider.Id, out var cached));
        Assert.Equal(newest.ObservationSequence, cached.ObservationSequence);
        Assert.Equal(75, cached.Fetch!.Usage.Primary.UsedPercent);
        Assert.True(service.TryGetLastSuccessfulLiveResult(provider.Id, out var lastGood));
        Assert.Equal(newest.ObservationSequence, lastGood.ObservationSequence);
        var gate = new UsageObservationSequenceGate();
        Assert.True(gate.TryAccept(wrongAccount));
        Assert.True(gate.TryAccept(old));
        Assert.True(gate.TryAccept(newest));
        Assert.Same(newest, DashboardResultMerge.KeepNewerObservations([newest], [old])[0]);
        AssertHistoryBounded(service);
    }

    [Fact]
    public async Task CompletedNewerAccountResultSurvivesAnInterveningForeignFailure()
    {
        var blocked = new PlannedResult(10, blocked: true);
        var provider = new OrderedProvider([blocked, new(75), new(error: ProviderErrorKind.AuthRequired)]);
        var service = Service(provider);
        var olderCall = service.FetchAsync(provider.Id, force: true);
        UsageResult confirmed;
        UsageResult returned;
        DateTimeOffset cachedAt;
        try
        {
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            confirmed = await service.FetchAsync(provider.Id, force: true);
            cachedAt = CacheStamp(service);
            provider.Identity = "A";
            await service.FetchAsync(provider.Id, force: true);
            provider.Identity = "B";
        }
        finally
        {
            blocked.Release.TrySetResult();
            returned = await olderCall.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(confirmed.ObservationSequence, returned.ObservationSequence);
        Assert.Equal(75, returned.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(confirmed.ObservedAt, returned.ObservedAt);
        Assert.Equal(confirmed.LastConfirmedAt, returned.LastConfirmedAt);
        Assert.Equal(confirmed.Fetch!.FetchedAt, returned.Fetch.FetchedAt);
        Assert.True(returned.PublicationOrder > confirmed.PublicationOrder);
        Assert.Equal(cachedAt, CacheStamp(service));
        AssertHistoryBounded(service);
    }

    [Fact]
    public async Task SyntheticAccountTransitionDoesNotCountAsANewerAttemptByTheCurrentAccount()
    {
        var first = new PlannedResult(10, blocked: true);
        var foreign = new PlannedResult(75, blocked: true);
        var provider = new OrderedProvider([first, foreign]);
        var service = Service(provider);
        var originalCall = service.FetchAsync(provider.Id, force: true);
        Task<UsageResult>? foreignCall = null;
        UsageResult transition;
        UsageResult original;
        try
        {
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Identity = "A";
            foreignCall = service.FetchAsync(provider.Id, force: true);
            await foreign.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Identity = "B";
            foreign.Release.TrySetResult();
            transition = await foreignCall.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(transition.Ok);
            Assert.Equal(UsageObservationOrigin.AccountTransition, transition.ObservationOrigin);
        }
        finally
        {
            first.Release.TrySetResult();
            foreign.Release.TrySetResult();
            original = await originalCall.WaitAsync(TimeSpan.FromSeconds(10));
            if (foreignCall is not null) await foreignCall.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(original.Ok);
        Assert.Equal(1, original.ObservationSequence);
        Assert.Equal(10, original.Fetch!.Usage.Primary.UsedPercent);
        Assert.True(original.PublicationOrder > transition.PublicationOrder);
        AssertHistoryBounded(service);
    }

    [Fact]
    public async Task ObsoleteForeignCompletionReturnsAlreadyCurrentCanonicalInsteadOfAnError()
    {
        var foreign = new PlannedResult(10, blocked: true);
        var provider = new OrderedProvider([foreign, new(75)]) { Identity = "A" };
        var service = Service(provider);
        var oldCall = service.FetchAsync(provider.Id, force: true);
        UsageResult current;
        UsageResult returned;
        try
        {
            await foreign.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Identity = "B";
            current = await service.FetchAsync(provider.Id, force: true);
        }
        finally
        {
            foreign.Release.TrySetResult();
            returned = await oldCall.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(returned.Ok);
        Assert.Equal(current.ObservationSequence, returned.ObservationSequence);
        Assert.Equal(current.PublicationOrder, returned.PublicationOrder);
        Assert.Equal(75, returned.Fetch!.Usage.Primary.UsedPercent);
        AssertHistoryBounded(service);
    }

    [Fact]
    public async Task CancellingTheOldestRequestReleasesHistoricalAccountCanonicals()
    {
        var first = new PlannedResult(10, blocked: true);
        var provider = new OrderedProvider([first, new(75), new(error: ProviderErrorKind.AuthRequired)]);
        var service = Service(provider);
        using var cancellation = new CancellationTokenSource();
        var oldCall = service.FetchAsync(provider.Id, force: true, cancellation.Token);
        try
        {
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.FetchAsync(provider.Id, force: true);
            provider.Identity = "A";
            await service.FetchAsync(provider.Id, force: true);
        }
        finally
        {
            cancellation.Cancel();
            first.Release.TrySetResult();
            try { await oldCall.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldCall);
        AssertHistoryBounded(service);
    }

    [Fact]
    public async Task DuplicateOldCompletionsReturnTheCurrentCanonicalWithoutNewRevisions()
    {
        var first = new PlannedResult(10, blocked: true);
        var second = new PlannedResult(20, blocked: true);
        var provider = new OrderedProvider([first, second, new(error: ProviderErrorKind.AuthRequired), new(75)]);
        var service = Service(provider);
        var firstCall = service.FetchAsync(provider.Id, force: true);
        Task<UsageResult>? secondCall = null;
        UsageResult current;
        try
        {
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondCall = service.FetchAsync(provider.Id, force: true);
            await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Identity = "A";
            await service.FetchAsync(provider.Id, force: true);
            provider.Identity = "B";
            current = await service.FetchAsync(provider.Id, force: true);
        }
        finally
        {
            first.Release.TrySetResult();
            second.Release.TrySetResult();
            await firstCall.WaitAsync(TimeSpan.FromSeconds(10));
            if (secondCall is not null) await secondCall.WaitAsync(TimeSpan.FromSeconds(10));
        }

        foreach (var result in new[] { await firstCall, await secondCall! })
        {
            Assert.Equal(current.ObservationSequence, result.ObservationSequence);
            Assert.Equal(current.PublicationOrder, result.PublicationOrder);
            Assert.Equal(75, result.Fetch!.Usage.Primary.UsedPercent);
        }
        AssertHistoryBounded(service);
    }

    [Fact]
    public async Task UncacheableAuthenticationErrorStillAdvancesTheDeliveryFrontier()
    {
        var blocked = new PlannedResult(10, blocked: true);
        var provider = new OrderedProvider([blocked, new(error: ProviderErrorKind.AuthRequired)]);
        var service = Service(provider);
        var olderCall = service.FetchAsync(provider.Id, force: true);
        UsageResult unavailable;
        UsageResult restored;
        try
        {
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Identity = null;
            unavailable = await service.FetchAsync(provider.Id, force: true);
            Assert.Equal(ProviderErrorKind.AuthRequired, unavailable.ErrorKind);
            Assert.False(service.TryGetCached(provider.Id, out _));
            provider.Identity = "B";
        }
        finally
        {
            blocked.Release.TrySetResult();
            restored = await olderCall.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(restored.Ok);
        Assert.Equal(1, restored.ObservationSequence);
        Assert.True(restored.PublicationOrder > unavailable.PublicationOrder);
        var gate = new UsageObservationSequenceGate();
        Assert.True(gate.TryAccept(unavailable));
        Assert.True(gate.TryAccept(restored));
        AssertHistoryBounded(service);
    }

    private static UsageService Service(OrderedProvider provider)
    {
        var service = new UsageService();
        service.Register(provider);
        return service;
    }

    private static void AssertHistoryBounded(UsageService service)
    {
        var field = typeof(UsageService).GetField("_completedResults",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.InRange(((System.Collections.IDictionary)field.GetValue(service)!).Count, 0, 1);
        var pending = typeof(UsageService).GetField("_inFlightSequences",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Empty((System.Collections.IDictionary)pending.GetValue(service)!);
    }

    private static DateTimeOffset CacheStamp(UsageService service)
    {
        var field = typeof(UsageService).GetField("_cache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var cache = (System.Collections.IDictionary)field.GetValue(service)!;
        var entry = cache[ProviderId.Copilot]!;
        return (DateTimeOffset)entry.GetType().GetProperty("At")!.GetValue(entry)!;
    }

    private sealed class PlannedResult(double quota = 0, ProviderErrorKind? error = null, bool blocked = false)
    {
        internal double Quota { get; } = quota;
        internal ProviderErrorKind? Error { get; } = error;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = ReleaseGate(blocked);
        private static TaskCompletionSource ReleaseGate(bool blocked)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!blocked) gate.TrySetResult();
            return gate;
        }
    }

    private sealed class OrderedProvider(PlannedResult[] plans) : IUsageProvider, IUsageCacheIdentityProvider
    {
        private int _calls;
        internal string? Identity { get; set; } = "B";
        public ProviderId Id => ProviderId.Copilot;
        public string DisplayName => "Test";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;
        public string GetCurrentCacheIdentity() => Identity
            ?? throw new ProviderException(ProviderErrorKind.AuthRequired, "Missing test credentials");
        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var plan = plans[Interlocked.Increment(ref _calls) - 1];
            var identity = Identity;
            plan.Started.TrySetResult();
            await plan.Release.Task.WaitAsync(ct);
            if (plan.Error is { } error) throw new ProviderException(error, "Test failure");
            return new ProviderFetchResult(new UsageSnapshot(new RateWindow(plan.Quota))
            {
                UsageHistory = new UsageHistory(),
            }, "test", cacheIdentity: identity);
        }
    }
}
