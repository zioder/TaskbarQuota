using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public class SessionTopologyRecoveryTests
{
    [Theory]
    [InlineData(SessionTopologyWatcher.WtsRemoteConnect, (int)TopologyChangeKind.SessionConnect)]
    [InlineData(SessionTopologyWatcher.WtsRemoteDisconnect, (int)TopologyChangeKind.SessionDisconnect)]
    [InlineData(SessionTopologyWatcher.WtsConsoleConnect, (int)TopologyChangeKind.SessionConnect)]
    [InlineData(SessionTopologyWatcher.WtsConsoleDisconnect, (int)TopologyChangeKind.SessionDisconnect)]
    [InlineData(SessionTopologyWatcher.WtsSessionLogon, (int)TopologyChangeKind.SessionConnect)]
    [InlineData(SessionTopologyWatcher.WtsSessionUnlock, (int)TopologyChangeKind.SessionUnlock)]
    public void Session_changes_that_replace_desktop_hosts_request_recovery(
        int code,
        int expectedKind)
    {
        Assert.True(SessionTopologyWatcher.TryMapSessionChange(code, out var change));
        Assert.Equal((TopologyChangeKind)expectedKind, change.Kind);
        Assert.True(change.RequiresHostReset);
    }

    [Fact]
    public void Unrelated_session_change_is_ignored()
        => Assert.False(SessionTopologyWatcher.TryMapSessionChange(0x7fff, out _));

    [Theory]
    [InlineData(0, 250)]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(3, 2000)]
    [InlineData(4, 4000)]
    [InlineData(20, 4000)]
    public void Recovery_retry_uses_bounded_backoff(int attempts, int expectedMilliseconds)
        => Assert.Equal(expectedMilliseconds, SessionTopologyWatcher.RetryDelay(attempts).TotalMilliseconds);

    [Fact]
    public void Recovery_waits_for_two_identical_non_empty_topologies()
    {
        var tracker = new TopologyStabilityTracker();

        Assert.False(tracker.Observe("primary"));
        Assert.False(tracker.Observe("primary|secondary"));
        Assert.True(tracker.Observe("primary|secondary"));
    }

    [Fact]
    public void Empty_topology_resets_stability_candidate()
    {
        var tracker = new TopologyStabilityTracker();

        Assert.False(tracker.Observe("primary|secondary"));
        Assert.False(tracker.Observe(string.Empty));
        Assert.False(tracker.Observe("primary|secondary"));
        Assert.True(tracker.Observe("primary|secondary"));
    }

    [Fact]
    public void One_missing_secondary_scan_does_not_destroy_a_live_host()
    {
        Assert.False(TaskBarManager.ShouldRemoveMissingTaskbar(1, hostAlive: true));
        Assert.True(TaskBarManager.ShouldRemoveMissingTaskbar(2, hostAlive: true));
        Assert.True(TaskBarManager.ShouldRemoveMissingTaskbar(1, hostAlive: false));
    }

    [Theory]
    [InlineData(2160, 1080, 1080, false)]
    [InlineData(2161, 1080, 1080, true)]
    [InlineData(0, -1080, 1080, false)]
    [InlineData(1, -1080, 1080, true)]
    public void Auto_hide_detection_uses_the_monitor_origin(
        int taskbarBottom,
        int displayTop,
        int displayHeight,
        bool expected)
        => Assert.Equal(
            expected,
            TaskbarStructureWatcher.IsAutoHideTaskbarOffscreen(taskbarBottom, displayTop, displayHeight));
}
