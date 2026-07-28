using TaskbarQuota.Services;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class QuotaReplenishmentEvaluatorTests
{
    [Fact]
    public void Evaluate_IncreaseBelowThreshold_DoesNotCreateEvent()
        => Assert.Null(QuotaReplenishmentEvaluator.Evaluate(
            Observation(80, 1),
            Observation(70.1, 2),
            Now()));

    [Fact]
    public void Evaluate_IncreaseAtThreshold_CreatesNeutralEvent()
    {
        var replenishment = QuotaReplenishmentEvaluator.Evaluate(
            Observation(80, 1),
            Observation(70, 2),
            Now());

        Assert.NotNull(replenishment);
        Assert.Equal(QuotaReplenishmentKind.AvailabilityIncrease, replenishment.Kind);
        Assert.Equal(10, replenishment.Increase);
    }

    [Fact]
    public void Evaluate_NormalConsumption_DoesNotCreateEvent()
        => Assert.Null(QuotaReplenishmentEvaluator.Evaluate(
            Observation(40, 1),
            Observation(55, 2),
            Now()));

    [Fact]
    public void Evaluate_ExactFullAvailability_IsFullReplenishment()
    {
        var replenishment = QuotaReplenishmentEvaluator.Evaluate(
            Observation(88, 1),
            Observation(0, 2),
            Now());

        Assert.Equal(QuotaReplenishmentKind.FullReplenishment, replenishment!.Kind);
    }

    [Fact]
    public void Evaluate_AdvancedCycleAfterDueTime_IsConfirmedRenewal()
    {
        var replenishment = QuotaReplenishmentEvaluator.Evaluate(
            Observation(88, 1, resetAt: Now().AddMinutes(-1)),
            Observation(4, 2, resetAt: Now().AddDays(7)),
            Now());

        Assert.Equal(QuotaReplenishmentKind.ConfirmedCycleRenewal, replenishment!.Kind);
    }

    [Fact]
    public void Evaluate_ResetChangeWithoutAvailabilityIncrease_DoesNotCreateEvent()
        => Assert.Null(QuotaReplenishmentEvaluator.Evaluate(
            Observation(50, 1, resetAt: Now().AddMinutes(-1)),
            Observation(50, 2, resetAt: Now().AddDays(7)),
            Now()));

    [Fact]
    public void Evaluate_StableResetWithIncreaseUsesNeutralClassification()
    {
        var resetAt = Now().AddDays(2);

        var replenishment = QuotaReplenishmentEvaluator.Evaluate(
            Observation(88, 1, resetAt: resetAt),
            Observation(70, 2, resetAt: resetAt),
            Now());

        Assert.Equal(QuotaReplenishmentKind.AvailabilityIncrease, replenishment!.Kind);
    }

    [Fact]
    public void Evaluate_FutureResetMovedLaterUsesNeutralClassification()
    {
        var replenishment = QuotaReplenishmentEvaluator.Evaluate(
            Observation(88, 1, resetAt: Now().AddDays(1)),
            Observation(70, 2, resetAt: Now().AddDays(8)),
            Now());

        Assert.Equal(QuotaReplenishmentKind.AvailabilityIncrease, replenishment!.Kind);
    }

    [Fact]
    public void Evaluate_ResetDisappearanceIsIncomparable()
        => Assert.Null(QuotaReplenishmentEvaluator.Evaluate(
            Observation(88, 1, resetAt: Now().AddMinutes(-1)),
            Observation(4, 2, resetAt: null),
            Now()));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Evaluate_InvalidPercent_DoesNotCreateEvent(double usedPercent)
        => Assert.Null(QuotaReplenishmentEvaluator.Evaluate(
            Observation(88, 1),
            Observation(usedPercent, 2),
            Now()));

    [Fact]
    public void Tracker_FirstLiveObservationOnlyCreatesBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();

        var events = tracker.Observe(LiveResult(1, primaryUsed: 88), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_LiveToLiveSignificantIncreaseCreatesEvent()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, primaryUsed: 88), Now());

        var events = tracker.Observe(LiveResult(2, primaryUsed: 4), Now());

        var replenishment = Assert.Single(events);
        Assert.Equal(12, replenishment.Previous.AvailablePercent);
        Assert.Equal(96, replenishment.Current.AvailablePercent);
    }

    [Fact]
    public void Tracker_SmallIncreasesAccumulateFromLowWater()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, primaryUsed: 88), Now());
        Assert.Empty(tracker.Observe(LiveResult(2, primaryUsed: 82), Now()));

        var events = tracker.Observe(LiveResult(3, primaryUsed: 78), Now());

        var replenishment = Assert.Single(events);
        Assert.Equal(12, replenishment.Previous.AvailablePercent);
        Assert.Equal(22, replenishment.Current.AvailablePercent);
    }

    [Fact]
    public void Tracker_LatchesEpisodeUntilAvailabilityDropsByThreshold()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, primaryUsed: 88), Now());
        Assert.Single(tracker.Observe(LiveResult(2, primaryUsed: 78), Now()));
        Assert.Empty(tracker.Observe(LiveResult(3, primaryUsed: 68), Now()));
        Assert.Empty(tracker.Observe(LiveResult(4, primaryUsed: 89), Now()));

        var events = tracker.Observe(LiveResult(5, primaryUsed: 79), Now());

        Assert.Single(events);
    }

    [Fact]
    public void Tracker_CacheThenLiveOnlyEstablishesFirstLiveBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, primaryUsed: 88).AsMemoryCache(), Now());

        var events = tracker.Observe(LiveResult(2, primaryUsed: 4), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_CacheReplayDoesNotMoveExistingLiveBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, primaryUsed: 88), Now());
        _ = tracker.Observe(LiveResult(1, primaryUsed: 4).AsMemoryCache(), Now());

        var events = tracker.Observe(LiveResult(2, primaryUsed: 4), Now());

        Assert.Single(events);
    }

    [Fact]
    public void Tracker_PendingResultDoesNotInvalidateExistingLiveBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, primaryUsed: 88), Now());
        _ = tracker.Observe(UsageResult.Pending(ProviderId.Codex, Provider(), "Loading..."), Now());

        var events = tracker.Observe(LiveResult(2, primaryUsed: 4), Now());

        Assert.Single(events);
    }

    [Fact]
    public void Tracker_RestoredSnapshotThenLiveOnlyEstablishesBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(BaseResult(primaryUsed: 88).AsStale(), Now());

        var events = tracker.Observe(LiveResult(2, primaryUsed: 4), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_FailureInvalidatesLiveBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, primaryUsed: 88), Now());
        _ = tracker.Observe(UsageResult.Failure(ProviderId.Codex, "boom", Provider()), Now());

        var events = tracker.Observe(LiveResult(2, primaryUsed: 4), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_FailureFallbackInvalidatesLiveBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        var baseline = LiveResult(1, primaryUsed: 88);
        _ = tracker.Observe(baseline, Now());
        _ = tracker.Observe(baseline.AsFailureFallback(2, Now()), Now());

        var events = tracker.Observe(LiveResult(3, primaryUsed: 4), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_OlderLiveResultIsIgnored()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(2, primaryUsed: 50), Now());

        var oldEvents = tracker.Observe(LiveResult(1, primaryUsed: 90), Now());
        var currentEvents = tracker.Observe(LiveResult(3, primaryUsed: 40), Now());

        Assert.Empty(oldEvents);
        Assert.Single(currentEvents);
    }

    [Fact]
    public void Tracker_DisappearingWindowReappearsAsFirstObservation()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, primaryUsed: 50, secondaryUsed: 90), Now());
        _ = tracker.Observe(LiveResult(2, primaryUsed: 50), Now());

        var events = tracker.Observe(LiveResult(3, primaryUsed: 50, secondaryUsed: 0), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_StableExtraIdsSurviveReordering()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, 50, extras:
        [
            ("a", "Alpha", 90),
            ("b", "Beta", 80),
        ]), Now());

        var events = tracker.Observe(LiveResult(2, 50, extras:
        [
            ("b", "Beta", 80),
            ("a", "Alpha", 70),
        ]), Now());

        var replenishment = Assert.Single(events);
        Assert.Equal("extra:a", replenishment.Current.Key.WindowId);
    }

    [Fact]
    public void Tracker_TitleChangeResetsExtraBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, 50, extras: [("model-0", "Alpha", 90)]), Now());

        var events = tracker.Observe(LiveResult(2, 50, extras: [("model-0", "Beta", 0)]), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_ResetDisappearanceAndReappearanceEachResetBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(
            LiveResult(1, 88, primaryResetAt: Now().AddHours(1)),
            Now());

        Assert.Empty(tracker.Observe(LiveResult(2, 4, primaryResetAt: null), Now()));
        Assert.Empty(tracker.Observe(LiveResult(3, 0, primaryResetAt: Now().AddDays(7)), Now()));
    }

    [Fact]
    public void Tracker_WindowDurationChangeResetsBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, 88, primaryWindowMinutes: 300), Now());

        var events = tracker.Observe(LiveResult(2, 4, primaryWindowMinutes: 10080), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_IdentifiableAccountChangeResetsBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, 88, email: "first@example.test"), Now());

        var events = tracker.Observe(LiveResult(2, 4, email: "second@example.test"), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Tracker_ResetRequiresANewBaseline()
    {
        var tracker = new QuotaReplenishmentTracker();
        _ = tracker.Observe(LiveResult(1, primaryUsed: 88), Now());
        tracker.Reset("test");

        var events = tracker.Observe(LiveResult(2, primaryUsed: 4), Now());

        Assert.Empty(events);
    }

    [Fact]
    public void Catalog_SkipsEmptyAndDuplicateExtraIds()
    {
        var result = LiveResult(1, 50, extras:
        [
            ("", "Empty", 90),
            ("dup", "First", 90),
            ("dup", "Second", 0),
        ]);

        var windows = QuotaAlertWindowCatalog.Enumerate(result).ToArray();

        Assert.Single(windows);
        Assert.Equal("primary", windows[0].Id);
    }

    [Fact]
    public void Catalog_UsesProviderLabelsAndWindowOverrides()
    {
        var provider = new LabelProvider(ProviderId.Devin, "Weekly", "Daily");
        var usage = new UsageSnapshot(new RateWindow(20))
        {
            Secondary = new RateWindow(30),
            ModelSpecific = new RateWindow(40, label: "Code review"),
            Monthly = new RateWindow(50),
        };
        var result = UsageResult.Success(provider.Id, provider, new ProviderFetchResult(usage, "test"));

        var titles = QuotaAlertWindowCatalog.Enumerate(result).Select(window => window.Title).ToArray();

        Assert.Equal(["Weekly", "Daily", "Code review", "Monthly"], titles);
    }

    [Fact]
    public void Catalog_UsesAntigravityWindowConventions()
    {
        var provider = new LabelProvider(ProviderId.Antigravity, "Gemini", "Non-Gemini");
        var usage = new UsageSnapshot(new RateWindow(20))
        {
            Secondary = new RateWindow(30),
            ModelSpecific = new RateWindow(40),
            Monthly = new RateWindow(50),
        };
        var result = UsageResult.Success(provider.Id, provider, new ProviderFetchResult(usage, "test"));

        var titles = QuotaAlertWindowCatalog.Enumerate(result).Select(window => window.Title).ToArray();

        Assert.Equal(["Gemini Weekly", "Non-Gemini Weekly", "Gemini 5h", "Non-Gemini 5h"], titles);
    }

    private static QuotaWindowObservation Observation(
        double used,
        long sequence,
        string title = "Weekly",
        DateTimeOffset? resetAt = null)
        => new(
            new QuotaWindowKey(ProviderId.Codex, "secondary"),
            title,
            used,
            10080,
            resetAt,
            sequence,
            Now());

    private static UsageResult LiveResult(
        long sequence,
        double primaryUsed,
        double? secondaryUsed = null,
        (string Id, string Title, double Used)[]? extras = null,
        DateTimeOffset? primaryResetAt = null,
        int? primaryWindowMinutes = 300,
        string? email = null)
        => BaseResult(primaryUsed, secondaryUsed, extras, primaryResetAt, primaryWindowMinutes, email)
            .AsLiveObservation(sequence, Now());

    private static UsageResult BaseResult(
        double primaryUsed,
        double? secondaryUsed = null,
        (string Id, string Title, double Used)[]? extras = null,
        DateTimeOffset? primaryResetAt = null,
        int? primaryWindowMinutes = 300,
        string? email = null)
    {
        var usage = new UsageSnapshot(new RateWindow(
            primaryUsed,
            windowMinutes: primaryWindowMinutes,
            resetAt: primaryResetAt));
        usage.Email = email;
        if (secondaryUsed is { } secondary)
            usage.Secondary = new RateWindow(secondary, windowMinutes: 10080);
        foreach (var extra in extras ?? [])
            usage.ExtraRateWindows.Add(new NamedRateWindow(
                extra.Id,
                extra.Title,
                new RateWindow(extra.Used, windowMinutes: 10080)));

        return UsageResult.Success(ProviderId.Codex, Provider(), new ProviderFetchResult(usage, "test"));
    }

    private static IUsageProvider Provider() => new TestProvider();

    private static DateTimeOffset Now()
        => new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

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

    private sealed class LabelProvider(
        ProviderId id,
        string sessionLabel,
        string weeklyLabel) : IUsageProvider
    {
        public ProviderId Id => id;
        public string DisplayName => id.ToString();
        public string SessionLabel => sessionLabel;
        public string WeeklyLabel => weeklyLabel;
        public BillingKind Billing => BillingKind.Subscription;

        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
