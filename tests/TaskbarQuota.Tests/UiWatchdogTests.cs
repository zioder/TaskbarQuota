using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public class UiWatchdogTests
{
    [Fact]
    public void RequestsRestartAfterThreeMisses()
        => Assert.True(TaskBarManager.ShouldRestartUiWatchdog(3, isQuitting: false, restartRequested: false));

    [Fact]
    public void DoesNotRestartBeforeThreshold()
        => Assert.False(TaskBarManager.ShouldRestartUiWatchdog(2, isQuitting: false, restartRequested: false));

    [Fact]
    public void DoesNotRestartWhileQuittingOrAfterARequest()
    {
        Assert.False(TaskBarManager.ShouldRestartUiWatchdog(3, isQuitting: true, restartRequested: false));
        Assert.False(TaskBarManager.ShouldRestartUiWatchdog(3, isQuitting: false, restartRequested: true));
    }
}
