using System.Reflection;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class ResetCreditPersistenceRefreshTests
{
    [Fact]
    public async Task UnchangedLiveCreditsRefreshDiskBeforeTheirTenMinuteConfirmationExpires()
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TaskbarQuota-credit-refresh-" + Guid.NewGuid().ToString("N")));
        var now = DateTimeOffset.UtcNow;
        var provider = new TestProvider(now.AddMinutes(-6), now);
        var service = new UsageService(directory);
        service.Register(provider);
        try
        {
            var first = await service.FetchAsync(provider.Id, force: true);
            await PendingWrites(service).WaitAsync(TimeSpan.FromSeconds(10));
            var queued = (Dictionary<ProviderId, DateTimeOffset>)typeof(UsageService)
                .GetField("_lastSnapshotQueuedAt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            // Simulate six minutes since the last write without sleeping or changing the host clock.
            queued[provider.Id] = now.AddMinutes(-6);

            var second = await service.FetchAsync(provider.Id, force: true);
            await PendingWrites(service).WaitAsync(TimeSpan.FromSeconds(10));
            var restored = UsageSnapshotStore.Load(directory, _ => provider)[provider.Id];

            Assert.Equal(first.Fetch!.FetchedAt, second.Fetch!.FetchedAt);
            Assert.Equal(now, restored.Fetch!.Usage.ResetCredits!.ConfirmedAt);
            Assert.Equal(now, restored.Fetch.Usage.ResetCredits.CachedAt);
            Assert.Equal(1, restored.Fetch.Usage.ResetCredits.AvailableCount);
        }
        finally
        {
            await PendingWrites(service).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Task PendingWrites(UsageService service)
        => (Task)typeof(UsageService).GetField("_snapshotWriteTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private sealed class TestProvider(params DateTimeOffset[] confirmations) : IUsageProvider
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
                ResetCredits = new ResetCreditsSnapshot(1, [], confirmedAt: confirmations[_calls++]),
            }, "test"));
    }
}
