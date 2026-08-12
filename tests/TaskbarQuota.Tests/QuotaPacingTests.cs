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
    public void MissingWindowMetadataIsUntracked()
    {
        var result = QuotaPacing.Evaluate(new RateWindow(50, resetAt: Now.AddHours(1)), Now);
        Assert.Equal(PaceSeverity.Untracked, result.Severity);
    }
}
