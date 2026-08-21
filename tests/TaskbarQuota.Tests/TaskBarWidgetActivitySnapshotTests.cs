using TaskbarQuota.AgentActivity;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class TaskBarWidgetActivitySnapshotTests
{
    [Fact]
    public void ScreenPolicyEmptyDoesNotUseScannerGracePeriod()
    {
        var visible = Snapshot("visible");
        var routedEmpty = new AgentActivitySnapshot([]);

        Assert.False(TaskBarWidget.ShouldDeferEmptyActivitySnapshot(
            visible,
            routedEmpty,
            allowEmptyGrace: false));
    }

    [Fact]
    public void TransientScannerEmptyKeepsExistingGracePeriod()
    {
        var visible = Snapshot("visible");
        var scannerEmpty = new AgentActivitySnapshot([]);

        Assert.True(TaskBarWidget.ShouldDeferEmptyActivitySnapshot(
            visible,
            scannerEmpty,
            allowEmptyGrace: true));
    }

    private static AgentActivitySnapshot Snapshot(string id)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentActivitySnapshot([
            new AgentActivityItem(
                id,
                ProviderId.Codex,
                "Codex",
                "Working",
                AgentActivityStatus.Working,
                now,
                now),
        ]);
    }
}
