using System.Reflection;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class LastSuccessfulPublicationOrderingTests
{
    [Fact]
    public async Task ValidationFromPreviousAccountCannotOverwriteANewerAcceptedLastGood()
    {
        var provider = new ControlledProvider("C");
        var service = Service(provider);
        var previous = await service.FetchAsync(provider.Id, force: true).WaitAsync(TimeSpan.FromSeconds(10));
        var store = typeof(UsageService).GetMethod("StoreLastSuccessfulLiveResult", BindingFlags.Instance | BindingFlags.NonPublic)!;
        provider.BlockNextIdentityRead();
        // Pause the real last-good writer after it captures the old credential identity, before
        // IsCurrent returns and it enters the state lock. Other fetches remain free to complete.
        var staleWrite = Task.Run(() => store.Invoke(service, [provider.Id, previous]));
        UsageResult current;
        try
        {
            await provider.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Identity = "B";
            current = await service.FetchAsync(provider.Id, force: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(current.Ok);
            Assert.True(current.PublicationOrder > previous.PublicationOrder);
        }
        finally
        {
            provider.ReleaseValidation.TrySetResult();
            await staleWrite.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(provider.ValidationTimedOut);
        Assert.True(service.TryGetLastSuccessfulLiveResult(provider.Id, out var lastGood));
        Assert.Equal(current.CacheIdentity, lastGood.CacheIdentity);
        Assert.Equal(current.ObservationSequence, lastGood.ObservationSequence);
        Assert.Equal(current.PublicationOrder, lastGood.PublicationOrder);
        Assert.Equal(75, lastGood.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(current.LastConfirmedAt, lastGood.LastConfirmedAt);
    }

    [Fact]
    public async Task CurrentAccountOlderAttemptCanReplaceForeignLastGoodAfterReissue()
    {
        var provider = new ControlledProvider("B", blockFirstFetch: true);
        var service = Service(provider);
        var olderCall = service.FetchAsync(provider.Id, force: true);
        UsageResult foreign;
        UsageResult current;
        try
        {
            await provider.FetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Identity = "A";
            foreign = await service.FetchAsync(provider.Id, force: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(foreign.Ok);
            provider.Identity = "B";
        }
        finally
        {
            provider.ReleaseFetch.TrySetResult();
            current = await olderCall.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(current.Ok);
        Assert.True(current.ObservationSequence < foreign.ObservationSequence);
        Assert.True(current.PublicationOrder > foreign.PublicationOrder);
        Assert.True(service.TryGetLastSuccessfulLiveResult(provider.Id, out var lastGood));
        Assert.Equal(current.CacheIdentity, lastGood.CacheIdentity);
        Assert.Equal(current.ObservationSequence, lastGood.ObservationSequence);
        Assert.Equal(current.PublicationOrder, lastGood.PublicationOrder);
        Assert.Equal(10, lastGood.Fetch!.Usage.Primary.UsedPercent);
    }

    private static UsageService Service(ControlledProvider provider)
    {
        var service = new UsageService();
        service.Register(provider);
        return service;
    }

    private sealed class ControlledProvider(string identity, bool blockFirstFetch = false) : IUsageProvider, IUsageCacheIdentityProvider
    {
        private volatile string _identity = identity;
        private int _blockNextRead;
        private int _calls;
        internal string Identity { get => _identity; set => _identity = value; }
        internal bool ValidationTimedOut { get; private set; }
        internal TaskCompletionSource ValidationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseValidation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FetchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFetch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderId Id => ProviderId.Copilot;
        public string DisplayName => "Test";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        internal void BlockNextIdentityRead() => Interlocked.Exchange(ref _blockNextRead, 1);

        public string GetCurrentCacheIdentity()
        {
            string captured = Identity;
            if (Interlocked.Exchange(ref _blockNextRead, 0) != 0)
            {
                ValidationStarted.TrySetResult();
                try { ReleaseValidation.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                catch (TimeoutException) { ValidationTimedOut = true; throw; }
            }
            return captured;
        }

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            int call = Interlocked.Increment(ref _calls);
            string captured = Identity;
            if (blockFirstFetch && call == 1)
            {
                FetchStarted.TrySetResult();
                await ReleaseFetch.Task.WaitAsync(ct);
            }
            return new ProviderFetchResult(new UsageSnapshot(new RateWindow(call == 1 ? 10 : 75))
            {
                UsageHistory = new UsageHistory(),
            }, "test", cacheIdentity: captured);
        }
    }
}
