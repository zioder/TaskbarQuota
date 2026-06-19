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
    public void Evaluate_DropsBelowThreshold_MarksStateChanged()
    {
        var state = new QuotaAlertState();
        var first = Now();

        _ = QuotaAlertEvaluator.Evaluate(Result(91), Settings(), state, first).ToArray();
        Assert.True(state.HasUnsavedChanges);

        state.ResetUnsavedChangesForTesting();
        Assert.False(state.HasUnsavedChanges);

        _ = QuotaAlertEvaluator.Evaluate(Result(10), Settings(), state, first.AddMinutes(1)).ToArray();

        Assert.True(state.HasUnsavedChanges);
    }

    [Fact]
    public void StateKey_WithoutResetAt_DoesNotUseChangingResetDescription()
    {
        var first = new RateWindow(91, resetDescription: "91/100 USD");
        var second = new RateWindow(92, resetDescription: "92/100 USD");

        var firstKey = QuotaAlertStateKey.For(ProviderId.Claude, "primary", 90, first);
        var secondKey = QuotaAlertStateKey.For(ProviderId.Claude, "primary", 90, second);

        Assert.Equal(firstKey, secondKey);
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

    [Fact]
    public void Evaluate_CodexResetCreditWithinFiveDays_Alerts()
    {
        var state = new QuotaAlertState();
        var expiresAt = Now().AddDays(5);

        var alerts = QuotaAlertEvaluator.Evaluate(CodexResetCreditResult(expiresAt), Settings(), state, Now()).ToArray();

        var alert = Assert.Single(alerts);
        Assert.Contains("reset credit expires soon", alert.Title);
        Assert.Contains("Oldest reset credit expires in 5d", alert.Body);
    }

    [Fact]
    public void Evaluate_CodexResetCreditBeyondFiveDays_DoesNotAlert()
    {
        var state = new QuotaAlertState();
        var expiresAt = Now().AddDays(5).AddMinutes(1);

        var alerts = QuotaAlertEvaluator.Evaluate(CodexResetCreditResult(expiresAt), Settings(), state, Now()).ToArray();

        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_CodexResetCreditExpiry_AlertsOncePerExpiry()
    {
        var state = new QuotaAlertState();
        var expiresAt = Now().AddDays(4);

        var first = QuotaAlertEvaluator.Evaluate(CodexResetCreditResult(expiresAt), Settings(), state, Now()).ToArray();
        var second = QuotaAlertEvaluator.Evaluate(CodexResetCreditResult(expiresAt), Settings(), state, Now().AddHours(1)).ToArray();

        Assert.Single(first);
        Assert.Empty(second);
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

    private static UsageResult CodexResetCreditResult(DateTimeOffset expiresAt)
    {
        var provider = new CodexAlertTestProvider();
        var usage = new UsageSnapshot(new RateWindow(10, resetAt: Now().AddHours(1)))
        {
            ResetCredits = new ResetCreditsSnapshot(1,
            [
                new ResetCreditGrant("available", Now().AddDays(-25), expiresAt),
            ]),
        };

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

    private sealed class CodexAlertTestProvider : IUsageProvider
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
