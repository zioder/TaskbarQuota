using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public class TaskBarManagerWidgetSnapshotTests
{
    [Fact]
    public void NestedSnapshotsUseSeparateBuffers()
    {
        var outer = TaskBarManager.TakeWidgetSnapshot();
        var inner = TaskBarManager.TakeWidgetSnapshot();

        Assert.NotSame(outer, inner);

        TaskBarManager.ReleaseWidgetSnapshot();
        TaskBarManager.ReleaseWidgetSnapshot();
    }
}
