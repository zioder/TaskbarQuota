using TaskbarQuota.Services;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public sealed class QuotaReplenishmentCrossSessionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"taskbarquota-replenishment-state-{Guid.NewGuid():N}");

    private string StatePath =>
        Path.Combine(_directory, QuotaReplenishmentStateStore.FileName);

    [Fact]
    public void FirstLiveObservationPersistsWithoutCreatingAnEvent()
    {
        var tracker = Tracker();

        var events = tracker.Observe(Live(1, 88, Earlier()), Earlier());

        Assert.Empty(events);
        Assert.True(File.Exists(StatePath));
        Assert.False(File.Exists(StatePath + ".tmp"));
        var json = File.ReadAllText(StatePath);
        Assert.DoesNotContain("account@example.test", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            QuotaReplenishmentStateStore.HashIdentity("account@example.test")!,
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FirstLiveObservationNextSessionDetectsFullReplenishment()
    {
        Seed(usedPercent: 88);

        var events = Tracker().Observe(Live(1, 0, Now()), Now());

        var replenishment = Assert.Single(events);
        Assert.Equal(QuotaReplenishmentKind.FullReplenishment, replenishment.Kind);
        Assert.True(replenishment.IsCrossSession);
        Assert.Equal(12, replenishment.Previous.AvailablePercent);
        Assert.Equal(100, replenishment.Current.AvailablePercent);
    }

    [Fact]
    public void ManualFullResetBeforeScheduledRenewalIsFullReplenishment()
    {
        var scheduledReset = Now().AddDays(6);
        Seed(usedPercent: 88, resetAt: scheduledReset);

        var events = Tracker().Observe(
            Live(1, 0, Now(), resetAt: scheduledReset),
            Now());

        Assert.Equal(
            QuotaReplenishmentKind.FullReplenishment,
            Assert.Single(events).Kind);
    }

    [Fact]
    public void ConfirmedExpiredCycleIsClassifiedAsRenewal()
    {
        Seed(
            usedPercent: 88,
            resetAt: Now().AddMinutes(-1));

        var events = Tracker().Observe(
            Live(1, 4, Now(), resetAt: Now().AddDays(7)),
            Now());

        Assert.Equal(
            QuotaReplenishmentKind.ConfirmedCycleRenewal,
            Assert.Single(events).Kind);
    }

    [Fact]
    public void DifferentIdentityEstablishesNewBaselineSilently()
    {
        Seed(usedPercent: 88, email: "first@example.test");

        var events = Tracker().Observe(
            Live(1, 0, Now(), email: "second@example.test"),
            Now());

        Assert.Empty(events);
        var json = File.ReadAllText(StatePath);
        Assert.DoesNotContain("first@example.test", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("second@example.test", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            QuotaReplenishmentStateStore.HashIdentity("second@example.test")!,
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCurrentIdentityEstablishesNewBaselineSilently()
    {
        Seed(usedPercent: 88);

        var events = Tracker().Observe(
            Live(1, 0, Now(), email: null),
            Now());

        Assert.Empty(events);
    }

    [Theory]
    [InlineData("Changed", 300)]
    [InlineData("Session", 600)]
    public void ChangedWindowIdentityEstablishesNewBaselineSilently(
        string label,
        int windowMinutes)
    {
        Seed(usedPercent: 88);

        var events = Tracker().Observe(
            Live(1, 0, Now(), label: label, windowMinutes: windowMinutes),
            Now());

        Assert.Empty(events);
    }

    [Fact]
    public void ObservationOlderThanMaximumAgeIsIgnored()
    {
        var old = Now() - QuotaReplenishmentCrossSessionTracker.MaximumObservationAge - TimeSpan.FromMinutes(1);
        Seed(usedPercent: 88, observedAt: old);

        var events = Tracker().Observe(Live(1, 0, Now()), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void CacheReplayDoesNotConsumeOrReplaceStartupCandidate()
    {
        Seed(usedPercent: 88);
        var tracker = Tracker();

        Assert.Empty(tracker.Observe(Live(1, 0, Now()).AsMemoryCache(), Now()));
        var events = tracker.Observe(Live(2, 0, Now()), Now());

        Assert.Single(events);
    }

    [Fact]
    public void OtherNonLiveResultsDoNotConsumeOrReplaceStartupCandidate()
    {
        Seed(usedPercent: 88);
        var tracker = Tracker();

        Assert.Empty(tracker.Observe(Live(1, 0, Now()).AsStale(), Now()));
        Assert.Empty(tracker.Observe(
            Live(2, 0, Now()).AsFailureFallback(2, Now()),
            Now()));
        Assert.Empty(tracker.Observe(
            UsageResult.Pending(ProviderId.Codex, new TestProvider(), "Loading..."),
            Now()));

        var events = tracker.Observe(Live(3, 0, Now()), Now());

        Assert.Single(events);
    }

    [Fact]
    public void MultipleWindowsAreReturnedAsOneProviderCatchUpBatch()
    {
        Seed(usedPercent: 88, secondaryUsedPercent: 90);

        var events = Tracker().Observe(
            Live(1, 0, Now(), secondaryUsedPercent: 20),
            Now());

        Assert.Equal(2, events.Count);
        Assert.All(events, replenishment => Assert.True(replenishment.IsCrossSession));
        Assert.Equal(["primary", "secondary"], events.Select(item => item.Current.Key.WindowId));
    }

    [Fact]
    public void ConsumedCandidateCannotNotifyAgainAfterAnotherRestart()
    {
        Seed(usedPercent: 88);
        Assert.Single(Tracker().Observe(Live(1, 0, Now()), Now()));

        var nextSessionEvents = Tracker().Observe(
            Live(1, 0, Now().AddHours(1)),
            Now().AddHours(1));

        Assert.Empty(nextSessionEvents);
    }

    [Fact]
    public void OutOfOrderLiveResultCannotReplaceNewerPersistedValues()
    {
        Seed(usedPercent: 88);
        var tracker = Tracker();
        Assert.Single(tracker.Observe(Live(2, 0, Now()), Now()));

        Assert.Empty(tracker.Observe(Live(1, 80, Now().AddMinutes(1)), Now().AddMinutes(1)));
        Assert.Empty(Tracker().Observe(
            Live(1, 0, Now().AddHours(1)),
            Now().AddHours(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"Version":"invalid"}""")]
    public void InvalidStateFileFallsBackToFirstObservation(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath, contents);

        var events = Tracker().Observe(Live(1, 0, Now()), Now());

        Assert.Empty(events);
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public void ClearingStateMakesTheNextLiveReadingANewBaseline()
    {
        var tracker = Tracker();
        _ = tracker.Observe(Live(1, 88, Earlier()), Earlier());
        Assert.True(File.Exists(StatePath));

        tracker.Reset(clearPersisted: true);
        var events = tracker.Observe(Live(2, 0, Now()), Now());

        Assert.Empty(events);
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public void UnchangedObservationRefreshesPersistedConfirmationHourly()
    {
        Seed(usedPercent: 88);
        var initial = LoadPersisted();

        var tracker = Tracker();
        _ = tracker.Observe(Live(1, 88, Earlier().AddMinutes(30)), Earlier().AddMinutes(30));
        Assert.Equal(initial.ObservedAt, LoadPersisted().ObservedAt);

        _ = tracker.Observe(Live(2, 88, Earlier().AddHours(2)), Earlier().AddHours(2));
        Assert.Equal(Earlier().AddHours(2), LoadPersisted().ObservedAt);
    }

    [Fact]
    public void OlderObservationCannotReplaceNewerPersistedValues()
    {
        var store = new QuotaReplenishmentStateStore(StatePath);
        var newer = new PersistedQuotaProvider(
            ProviderId.Codex,
            QuotaReplenishmentStateStore.HashIdentity("account@example.test"),
            Now(),
            [new PersistedQuotaWindow("primary", "Session", 10, 300, null)]);
        Assert.True(store.Upsert(newer));

        var older = newer with
        {
            ObservedAt = Earlier(),
            Windows = [new PersistedQuotaWindow("primary", "Session", 90, 300, null)],
        };
        Assert.True(store.Upsert(older));

        var persisted = LoadPersisted();
        Assert.Equal(Now(), persisted.ObservedAt);
        Assert.Equal(10, Assert.Single(persisted.Windows).UsedPercent);
    }

    private void Seed(
        double usedPercent,
        double? secondaryUsedPercent = null,
        string? email = "account@example.test",
        DateTimeOffset? observedAt = null,
        DateTimeOffset? resetAt = null)
    {
        var when = observedAt ?? Earlier();
        var tracker = Tracker();
        Assert.Empty(tracker.Observe(
            Live(
                1,
                usedPercent,
                when,
                email,
                secondaryUsedPercent,
                resetAt),
            when));
    }

    private PersistedQuotaProvider LoadPersisted()
    {
        var store = new QuotaReplenishmentStateStore(StatePath);
        Assert.True(store.TryGet(ProviderId.Codex, out var observation));
        return observation;
    }

    private QuotaReplenishmentCrossSessionTracker Tracker()
        => new(new QuotaReplenishmentStateStore(StatePath));

    private static UsageResult Live(
        long sequence,
        double usedPercent,
        DateTimeOffset observedAt,
        string? email = "account@example.test",
        double? secondaryUsedPercent = null,
        DateTimeOffset? resetAt = null,
        string label = "Session",
        int windowMinutes = 300)
    {
        var provider = new TestProvider();
        var usage = new UsageSnapshot(new RateWindow(
            usedPercent,
            windowMinutes,
            resetAt,
            label: label))
        {
            Email = email,
        };
        if (secondaryUsedPercent is { } secondary)
            usage.Secondary = new RateWindow(secondary, windowMinutes: 10080);

        return UsageResult.Success(
                provider.Id,
                provider,
                new ProviderFetchResult(usage, "test", observedAt))
            .AsLiveObservation(sequence, observedAt);
    }

    private static DateTimeOffset Earlier() => Now().AddHours(-2);

    private static DateTimeOffset Now()
        => new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
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
