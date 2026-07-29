using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public class TaskBarManagerTests
{
    [Fact]
    public void CreateStableSnapshot_ReentrantCallDoesNotMutateEarlierSnapshot()
    {
        var source = new List<int> { 1, 2 };
        var outerSnapshot = TaskBarManager.CreateStableSnapshot(source);

        source.Clear();
        source.Add(3);
        var nestedSnapshot = TaskBarManager.CreateStableSnapshot(source);

        Assert.Equal([1, 2], outerSnapshot);
        Assert.Equal([3], nestedSnapshot);
        Assert.NotSame(outerSnapshot, nestedSnapshot);
    }
}
