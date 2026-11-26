namespace TaskbarQuota.Tests;

public class RecoveryRestartBudgetTests
{
    [Fact]
    public void ThreeRecoveryLaunchesExhaustTheCrossProcessChain()
    {
        var budget = new RecoveryRestartBudget(0);
        for (int attempt = 1; attempt <= RecoveryRestartBudget.MaximumAttempts; attempt++)
        {
            Assert.True(budget.TryGetNextAttempt(out int next));
            Assert.Equal(attempt, next);
            // A failed spawn can retry the same attempt; a successful child inherits it.
            Assert.True(budget.TryGetNextAttempt(out int retry));
            Assert.Equal(next, retry);
            budget = new RecoveryRestartBudget(next);
        }
        Assert.False(budget.TryGetNextAttempt(out _));
        Assert.True(new RecoveryRestartBudget(0).TryGetNextAttempt(out int manual));
        Assert.Equal(1, manual);
    }

    [Fact]
    public void ContinuousHealthyXamlTicksResetTheBudgetAfterTwoMinutes()
    {
        var budget = new RecoveryRestartBudget(3);
        for (int seconds = 0; seconds < 120; seconds += 5)
        {
            Assert.False(budget.ReportHealthyTick(TimeSpan.FromSeconds(seconds)));
            Assert.False(budget.TryGetNextAttempt(out _));
        }
        Assert.True(budget.ReportHealthyTick(TimeSpan.FromSeconds(120)));
        Assert.True(budget.TryGetNextAttempt(out int next));
        Assert.Equal(1, next);
    }

    [Fact]
    public void LongGapOrClockRegressionCannotCountAsHealthyUptime()
    {
        foreach (int jump in new[] { -1, 200 })
        {
            var budget = new RecoveryRestartBudget(3);
            budget.ReportHealthyTick(TimeSpan.Zero);
            Assert.False(budget.ReportHealthyTick(TimeSpan.FromSeconds(jump)));
            for (int seconds = 5; seconds < 120; seconds += 5)
                Assert.False(budget.ReportHealthyTick(TimeSpan.FromSeconds(jump + seconds)));
            Assert.True(budget.ReportHealthyTick(TimeSpan.FromSeconds(jump + 120)));
        }
    }
}
