using TaskbarQuota.Services;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class QuotaAlertServiceTests
{
    [Fact]
    public void ReplenishmentWorksWhenThresholdAlertsAreDisabled()
    {
        var notifier = new FakeNotifier();
        var settings = Settings(replenishmentEnabled: true);
        var service = new QuotaAlertService(
            notifier,
            clock: Now,
            settingsProvider: () => settings,
            state: new QuotaAlertState());

        service.OnStateChanged(Result(1, usedPercent: 88));
        service.OnStateChanged(Result(2, usedPercent: 4));

        var notification = Assert.Single(notifier.Notifications);
        Assert.Equal("Codex session quota increased", notification.Title);
        Assert.Equal("Available quota increased from 12% to 96%.", notification.Body);
    }

    [Fact]
    public void DisabledReplenishmentDoesNotNotifyOrRetainBaseline()
    {
        var notifier = new FakeNotifier();
        var settings = Settings(replenishmentEnabled: false);
        var service = new QuotaAlertService(
            notifier,
            clock: Now,
            settingsProvider: () => settings,
            state: new QuotaAlertState());

        service.OnStateChanged(Result(1, usedPercent: 88));
        settings = Settings(replenishmentEnabled: true);
        service.OnStateChanged(Result(2, usedPercent: 4));

        Assert.Empty(notifier.Notifications);
    }

    [Fact]
    public void DisablingAndReenablingReplenishmentRequiresANewBaseline()
    {
        var notifier = new FakeNotifier();
        var settings = Settings(replenishmentEnabled: true);
        var service = new QuotaAlertService(
            notifier,
            clock: Now,
            settingsProvider: () => settings,
            state: new QuotaAlertState());

        service.OnStateChanged(Result(1, usedPercent: 88));
        settings = Settings(replenishmentEnabled: false);
        service.OnSettingsChanged(null, EventArgs.Empty);
        settings = Settings(replenishmentEnabled: true);
        service.OnSettingsChanged(null, EventArgs.Empty);
        service.OnStateChanged(Result(2, usedPercent: 4));

        Assert.Empty(notifier.Notifications);
    }

    private static QuotaAlertSettings Settings(bool replenishmentEnabled) => new()
    {
        Enabled = false,
        ReplenishmentEnabled = replenishmentEnabled,
        WarningThreshold = 75,
        CriticalThreshold = 90,
        CooldownMinutes = 30,
    };

    private static UsageResult Result(long sequence, double usedPercent)
    {
        var provider = new TestProvider();
        var usage = new UsageSnapshot(new RateWindow(usedPercent, windowMinutes: 300));
        return UsageResult.Success(provider.Id, provider, new ProviderFetchResult(usage, "test"))
            .AsLiveObservation(sequence, Now());
    }

    private static DateTimeOffset Now()
        => new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeNotifier : IQuotaAlertNotifier
    {
        public List<QuotaAlertNotification> Notifications { get; } = new();

        public void Register()
        {
        }

        public bool Show(QuotaAlertNotification notification)
        {
            Notifications.Add(notification);
            return true;
        }
    }

    private sealed class TestProvider : IUsageProvider
    {
        public ProviderId Id => ProviderId.Codex;
        public string DisplayName => "Codex";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
