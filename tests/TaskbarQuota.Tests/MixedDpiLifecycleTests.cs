using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public class MixedDpiLifecycleTests
{
    [Fact]
    public void Unchanged_taskbar_poll_does_not_request_position_work()
    {
        Assert.Equal(
            TaskbarChangeReason.None,
            TaskbarStructureWatcher.DetectChangeReason(
                previousWidgets: true,
                previousCentered: true,
                previousHidden: false,
                currentWidgets: true,
                currentCentered: true,
                currentHidden: false));
    }

    [Theory]
    [InlineData(true, true, false, false, true, false, TaskbarChangeReason.WidgetsButton)]
    [InlineData(true, true, false, true, false, false, TaskbarChangeReason.Alignment)]
    [InlineData(true, true, false, true, true, true, TaskbarChangeReason.Visibility)]
    public void Changed_taskbar_poll_reports_the_responsible_reason(
        bool oldWidgets,
        bool oldCentered,
        bool oldHidden,
        bool newWidgets,
        bool newCentered,
        bool newHidden,
        TaskbarChangeReason expected)
    {
        Assert.Equal(
            expected,
            TaskbarStructureWatcher.DetectChangeReason(
                oldWidgets, oldCentered, oldHidden, newWidgets, newCentered, newHidden));
    }

    [Fact]
    public void Dpi_change_requires_two_matching_observations()
    {
        var debounce = new DpiChangeDebouncer();

        Assert.False(debounce.Observe(120, 168));
        Assert.True(debounce.Observe(120, 168));
    }

    [Fact]
    public void Dpi_change_rejects_flapping_and_resets_at_applied_value()
    {
        var debounce = new DpiChangeDebouncer();

        Assert.False(debounce.Observe(120, 168));
        Assert.False(debounce.Observe(120, 96));
        Assert.False(debounce.Observe(120, 120));
        Assert.False(debounce.Observe(120, 168));
        Assert.True(debounce.Observe(120, 168));
    }

    [Fact]
    public void Physical_width_preserves_logical_size_across_dpi_change()
    {
        // 300 physical px at 150% is 200 DIP; at 125% it should occupy 250 physical px.
        Assert.Equal(250, TaskBarWidget.RescalePhysicalWidth(300, 144, 120));
    }
}
