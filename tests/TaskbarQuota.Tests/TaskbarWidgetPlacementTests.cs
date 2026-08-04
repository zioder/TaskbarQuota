using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public sealed class TaskbarWidgetPlacementTests
{
    private static readonly IReadOnlyList<(int start, int end)> Gaps =
        new[] { (0, 1000) };

    [Fact]
    public void PlacePair_QuotaFirst_UsesRequestedAnchor()
    {
        var result = TaskbarWidgetPlacement.PlacePair(200, 180, 120, 8,
            TaskbarWidgetOrder.QuotaFirst, Gaps);

        Assert.Equal(new WidgetPairPlacement(200, 388, 308), result);
    }

    [Fact]
    public void PlacePair_ActivityFirst_ClampsToGap()
    {
        var result = TaskbarWidgetPlacement.PlacePair(900, 180, 120, 8,
            TaskbarWidgetOrder.ActivityFirst, Gaps);

        Assert.Equal(new WidgetPairPlacement(820, 692, 308), result);
    }

    [Fact]
    public void PlacePair_ReturnsNullWhenPairDoesNotFit()
    {
        var result = TaskbarWidgetPlacement.PlacePair(0, 180, 120, 8,
            TaskbarWidgetOrder.QuotaFirst, new[] { (0, 300) });

        Assert.Null(result);
    }

    [Fact]
    public void PlaceAdaptive_ExpandsToAvailableWidthUpToMaximum()
    {
        var result = TaskbarWidgetPlacement.PlaceAdaptive(
            preferredX: 200, currentWidth: 240, minimumWidth: 160, maximumWidth: 400,
            anchorRight: false, new[] { (0, 700) });

        Assert.Equal(new AdaptiveWidgetPlacement(200, 400), result);
    }

    [Fact]
    public void PlaceAdaptive_PreservesRightEdgeWhenWidgetIsLeftOfPartner()
    {
        var result = TaskbarWidgetPlacement.PlaceAdaptive(
            preferredX: 300, currentWidth: 240, minimumWidth: 160, maximumWidth: 400,
            anchorRight: true, new[] { (100, 540) });

        Assert.Equal(new AdaptiveWidgetPlacement(140, 400), result);
    }

    [Fact]
    public void PlaceAdaptive_UsesLaneWidthButNeverChoosesAnUndersizedLane()
    {
        var result = TaskbarWidgetPlacement.PlaceAdaptive(
            preferredX: 100, currentWidth: 240, minimumWidth: 160, maximumWidth: 400,
            anchorRight: false, new[] { (0, 120), (200, 480) });

        Assert.Equal(new AdaptiveWidgetPlacement(200, 280), result);
    }

    [Fact]
    public void OrderForDraggedActivity_SwapsAtPartnerCenter()
    {
        Assert.Equal(TaskbarWidgetOrder.ActivityFirst,
            TaskbarWidgetPlacement.OrderForDraggedWidget(TaskbarWidgetRole.Activity, 100, 100, 220, 100,
                TaskbarWidgetOrder.QuotaFirst));
        Assert.Equal(TaskbarWidgetOrder.QuotaFirst,
            TaskbarWidgetPlacement.OrderForDraggedWidget(TaskbarWidgetRole.Activity, 300, 100, 220, 100,
                TaskbarWidgetOrder.ActivityFirst));
    }

    [Fact]
    public void OrderForDraggedWidget_HoldsCurrentOrderInsideHysteresisBand()
    {
        Assert.Equal(TaskbarWidgetOrder.QuotaFirst,
            TaskbarWidgetPlacement.OrderForDraggedWidget(TaskbarWidgetRole.Activity, 211, 100, 220, 100,
                TaskbarWidgetOrder.QuotaFirst, hysteresis: 12));
        Assert.Equal(TaskbarWidgetOrder.ActivityFirst,
            TaskbarWidgetPlacement.OrderForDraggedWidget(TaskbarWidgetRole.Activity, 207, 100, 220, 100,
                TaskbarWidgetOrder.QuotaFirst, hysteresis: 12));
    }

    [Fact]
    public void StepAnimatedPosition_EasesTowardTargetWithoutOvershooting()
    {
        int position = 100;
        for (int i = 0; i < 30; i++)
        {
            int next = TaskbarWidgetPlacement.StepAnimatedPosition(position, 300);
            Assert.InRange(next, position, 300);
            position = next;
        }

        Assert.Equal(300, position);
    }
}
