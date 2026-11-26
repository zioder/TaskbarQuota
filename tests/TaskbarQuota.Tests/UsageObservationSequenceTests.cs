using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class UsageObservationSequenceTests
{
    [Fact]
    public async Task LateSuccessfulCallReturnsNewerAuthenticationFailureInsteadOfResurrectingSuccess()
    {
        var provider = new OverlappingProvider();
        var service = new UsageService();
        service.Register(provider);
        var olderCall = service.FetchAsync(provider.Id, force: true);
        UsageResult newer;
        try
        {
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            newer = await service.FetchAsync(provider.Id, force: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(newer.Ok);
            Assert.Equal(ProviderErrorKind.AuthRequired, newer.ErrorKind);
        }
        finally
        {
            provider.Release.TrySetResult();
            await olderCall.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var older = await olderCall.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(newer.ObservationSequence, older.ObservationSequence);
        Assert.False(older.Ok);
        Assert.Equal(ProviderErrorKind.AuthRequired, older.ErrorKind);
        Assert.True(service.TryGetCached(provider.Id, out var cached));
        Assert.Equal(newer.ObservationSequence, cached.ObservationSequence);
        Assert.False(cached.Ok);
        Assert.False(service.TryGetLastSuccessfulLiveResult(provider.Id, out _));
    }

    [Fact]
    public void IdenticalNewerConfirmationStillBlocksAnOlderQueuedValue()
    {
        var provider = new OverlappingProvider();
        var first = Success(provider, 1, 25);
        var sameValue = Success(provider, 3, 25);
        var lateDifferentValue = Success(provider, 2, 80);
        var gate = new UsageObservationSequenceGate();

        Assert.True(gate.TryAccept(first));
        Assert.True(gate.TryAccept(sameValue));
        Assert.False(gate.TryAccept(lateDifferentValue));
        Assert.True(gate.TryAccept(sameValue.AsMemoryCache()));
    }

    [Fact]
    public void PooledTileTracksEachProviderAndDoesNotForgetPriorProviderSequence()
    {
        var codex = new OverlappingProvider();
        var claude = new OverlappingProvider(ProviderId.Claude);
        var gate = new UsageObservationSequenceGate();

        Assert.True(gate.TryAccept(Success(codex, 10, 25)));
        Assert.True(gate.TryAccept(Success(claude, 1, 40)));
        Assert.False(gate.TryAccept(Success(codex, 9, 80)));
        Assert.False(gate.TryAccept(UsageResult.Pending(codex.Id, codex, "Loading...")));
        Assert.True(gate.TryAccept(Success(codex, 11, 20)));
    }

    private static UsageResult Success(OverlappingProvider provider, long sequence, double percent)
        => UsageResult.Success(provider.Id, provider,
            new ProviderFetchResult(new UsageSnapshot(new RateWindow(percent)), "live"))
            .AsLiveObservation(sequence, DateTimeOffset.Now);

    private sealed class OverlappingProvider(ProviderId id = ProviderId.Codex) : IUsageProvider
    {
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderId Id => id;
        public string DisplayName => id.ToString();
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) != 1)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Expired");
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new ProviderFetchResult(new UsageSnapshot(new RateWindow(80)), "live");
        }
    }
}
