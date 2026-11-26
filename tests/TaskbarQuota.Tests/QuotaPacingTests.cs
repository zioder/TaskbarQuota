using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class QuotaPacingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HealthyForecastIncludesIdealPaceDelta()
    {
        var result = QuotaPacing.Evaluate(new RateWindow(30, 10080, Now.AddDays(3.5)), Now);
        Assert.Equal(PaceSeverity.Healthy, result.Severity);
        Assert.InRange(result.ProjectedUsedPercent!.Value, 59.9, 60.1);
        Assert.InRange(result.AheadBehindPercent!.Value, -40.1, -39.9);
    }

    [Fact]
    public void RunningOutForecastCalculatesEtaBeforeReset()
    {
        var window = new RateWindow(60, 10080, Now.AddDays(3.5));
        var result = QuotaPacing.Evaluate(window, Now);
        Assert.Equal(PaceSeverity.RunningOut, result.Severity);
        Assert.NotNull(result.RunOutAt);
        Assert.True(result.RunOutAt < window.ResetAt);
    }

    [Fact]
    public void FreshSessionIsUntracked()
    {
        var result = QuotaPacing.Evaluate(new RateWindow(0, 300, Now.AddHours(5)), Now, true);
        Assert.Equal(PaceSeverity.Untracked, result.Severity);
    }

    [Fact]
    public void SpentWindowTooltipSaysLimitReached()
    {
        var result = QuotaPacing.Evaluate(new RateWindow(100, 10080, Now.AddDays(1)), Now);
        Assert.Equal(PaceSeverity.Spent, result.Severity);
        Assert.Equal("Limit reached", QuotaPacing.Tooltip(result, Now));
    }

    [Fact]
    public void FractionalQuotaStillAvailableIsNotMarkedSpent()
    {
        var result = QuotaPacing.Evaluate(new RateWindow(99.6, 10080, Now.AddMinutes(1)), Now);
        Assert.Equal(PaceSeverity.Close, result.Severity);
        Assert.Equal("<1% spare", QuotaPacing.CardText(result, Now));
    }

    [Theory]
    [InlineData(48.75, "~2% spare")]
    [InlineData(49.75, "<1% spare")]
    [InlineData(50, "0% spare")]
    public void SpareForecastPreservesRoundingWithoutFakingAMinimum(double used, string expected)
    {
        var result = QuotaPacing.Evaluate(new RateWindow(used, 10080, Now.AddDays(3.5)), Now);
        Assert.Equal(PaceSeverity.Close, result.Severity);
        Assert.Equal(expected, QuotaPacing.CardText(result, Now));
        Assert.Equal(expected, QuotaPacing.WarningText(result, Now));
        Assert.EndsWith($"({expected})", QuotaPacing.Tooltip(result, Now));
    }

    [Fact]
    public void ClosePaceReportsSpareAndNoRunOut()
    {
        var result = QuotaPacing.Evaluate(new RateWindow(47.5, 10080, Now.AddDays(3.5)), Now);
        Assert.Equal(PaceSeverity.Close, result.Severity);
        Assert.Null(result.RunOutAt);
        Assert.InRange(result.IdealPacePercent!.Value, 49.9, 50.1);
    }

    [Fact]
    public void MissingWindowMetadataIsUntracked()
    {
        var result = QuotaPacing.Evaluate(new RateWindow(50, resetAt: Now.AddHours(1)), Now);
        Assert.Equal(PaceSeverity.Untracked, result.Severity);
    }
}
