using TaskbarQuota.AgentActivity;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota.Tests;

public class TaskBarWidgetActivitySnapshotTests
{
    [Fact]
    public void EquivalentSnapshotsDoNotRequestAnotherLayout()
    {
        var current = Snapshot("same");
        var next = new AgentActivitySnapshot(current.Items.Select(item => item with { }).ToArray());
        Assert.True(TaskBarWidget.IsActivitySnapshotUnchanged(current, next, current.CompactItems, next.CompactItems));
        Assert.False(TaskBarWidget.IsActivitySnapshotUnchanged(current, next, null, next.CompactItems));
    }

    [Fact]
    public void ChangedContentAndExpiredCompactItemsStillUpdateLayout()
    {
        var current = Snapshot("same");
        var changed = new AgentActivitySnapshot([current.Items[0] with { Status = AgentActivityStatus.Waiting }]);
        Assert.False(TaskBarWidget.IsActivitySnapshotUnchanged(current, changed, current.CompactItems, changed.CompactItems));
        Assert.False(TaskBarWidget.IsActivitySnapshotUnchanged(current, current, current.Items, []));
    }

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
