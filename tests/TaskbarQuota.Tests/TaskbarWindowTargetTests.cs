using TaskbarQuota.Interop;
using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public class TaskbarWindowTargetTests
{
    [Theory]
    [InlineData(TaskbarWindowTarget.PrimaryClassName, true)]
    [InlineData(TaskbarWindowTarget.SecondaryClassName, false)]
    public void IsTaskbarClassName_AcceptsWindowsTaskbarClasses(string className, bool expectedPrimary)
    {
        Assert.True(TaskbarWindowTarget.IsTaskbarClassName(className, out bool isPrimary));
        Assert.Equal(expectedPrimary, isPrimary);
    }

    [Fact]
    public void IsTaskbarClassName_RejectsOtherShellWindows()
    {
        Assert.False(TaskbarWindowTarget.IsTaskbarClassName("WorkerW", out bool isPrimary));
        Assert.False(isPrimary);
    }

    [Fact]
    public void BuildDisplayKey_UsesStableSanitizedDisplayId()
    {
        var displayKey = TaskbarWindowTarget.BuildDisplayKey(
            @"\\.\DISPLAY2",
            new RECT { left = 2560, top = 0, right = 4480, bottom = 1080 });

        Assert.Equal("DISPLAY2", displayKey);
        Assert.Equal("taskbar-widget-position-DISPLAY2.txt", TaskbarWindowTarget.BuildPositionFileName(displayKey));
    }

    [Fact]
    public void BuildDisplayKey_FallsBackToBoundsWhenDisplayIdIsUnavailable()
    {
        var displayKey = TaskbarWindowTarget.BuildDisplayKey(
            null,
            new RECT { left = -1920, top = 0, right = 0, bottom = 1080 });

        Assert.Equal("-1920_0_0_1080", displayKey);
        Assert.Equal("taskbar-widget-position--1920_0_0_1080.txt", TaskbarWindowTarget.BuildPositionFileName(displayKey));
    }
}
