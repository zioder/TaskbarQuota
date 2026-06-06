using TaskbarQuota.Services;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class QuotaAlertEvaluatorTests
{
    [Fact]
    public void Evaluate_DisabledSettings_DoesNotAlert()
    {
        var state = new QuotaAlertState();
        var result = Result(91);

        var alerts = QuotaAlertEvaluator.Evaluate(result, Settings(enabled: false), state, Now()).ToArray();

        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_AboveCritical_AlertsForCriticalOnly()
    {
        var state = new QuotaAlertState();
        var result = Result(91);

        var alerts = QuotaAlertEvaluator.Evaluate(result, Settings(), state, Now()).ToArray();

        var alert = Assert.Single(alerts);
        Assert.Contains("CRITICAL", alert.Body);
    }

    [Fact]
    public void Evaluate_WarningThenCritical_AlertsAtEachThreshold()
    {
        var state = new QuotaAlertState();
        var first = Now();

        var warning = QuotaAlertEvaluator.Evaluate(Result(80), Settings(), state, first).ToArray();
        var critical = QuotaAlertEvaluator.Evaluate(Result(91), Settings(), state, first.AddMinutes(1)).ToArray();

        Assert.Single(warning);
        Assert.Contains("WARNING", warning[0].Body);
        Assert.Single(critical);
        Assert.Contains("CRITICAL", critical[0].Body);
    }

    [Fact]
    public void Evaluate_RepeatedWithinCooldown_DoesNotAlertAgain()
    {
        var state = new QuotaAlertState();
        var first = Now();
        var result = Result(91);

        _ = QuotaAlertEvaluator.Evaluate(result, Settings(), state, first).ToArray();
        var second = QuotaAlertEvaluator.Evaluate(result, Settings(), state, first.AddMinutes(5)).ToArray();

        Assert.Empty(second);
    }

    [Fact]
    public void Evaluate_AfterCooldown_AlertsAgain()
    {
        var state = new QuotaAlertState();
        var first = Now();
        var result = Result(91);

        _ = QuotaAlertEvaluator.Evaluate(result, Settings(), state, first).ToArray();
        var second = QuotaAlertEvaluator.Evaluate(result, Settings(), state, first.AddMinutes(31)).ToArray();

        Assert.Single(second);
    }

    [Fact]
    public void Evaluate_DropsBelowThreshold_AllowsFutureCrossing()
    {
        var state = new QuotaAlertState();
        var first = Now();

        _ = QuotaAlertEvaluator.Evaluate(Result(91), Settings(), state, first).ToArray();
        _ = QuotaAlertEvaluator.Evaluate(Result(10), Settings(), state, first.AddMinutes(1)).ToArray();
        var alerts = QuotaAlertEvaluator.Evaluate(Result(91), Settings(), state, first.AddMinutes(2)).ToArray();

        Assert.Single(alerts);
    }

    [Fact]
    public void Settings_Normalized_KeepsCriticalAboveWarning()
    {
        var settings = new QuotaAlertSettings
        {
            Enabled = true,
            WarningThreshold = 90,
            CriticalThreshold = 80,
            CooldownMinutes = 0,
        }.Normalized();

        Assert.Equal(90, settings.WarningThreshold);
        Assert.Equal(91, settings.CriticalThreshold);
        Assert.Equal(1, settings.CooldownMinutes);
    }

    private static QuotaAlertSettings Settings(bool enabled = true) => new()
    {
        Enabled = enabled,
        WarningThreshold = 75,
        CriticalThreshold = 90,
        CooldownMinutes = 30,
    };

    private static DateTimeOffset Now()
        => new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);

    private static UsageResult Result(double primaryPercent)
    {
        var provider = new AlertTestProvider();
        var usage = new UsageSnapshot(new RateWindow(primaryPercent, resetAt: Now().AddHours(1)));
        return UsageResult.Success(provider.Id, provider, new ProviderFetchResult(usage, "test"));
    }

    private sealed class AlertTestProvider : IUsageProvider
    {
        public ProviderId Id => ProviderId.Claude;
        public string DisplayName => "Claude";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
