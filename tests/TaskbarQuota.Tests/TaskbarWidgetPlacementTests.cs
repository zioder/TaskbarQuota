using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public sealed class TaskbarWidgetPlacementTests
{
    private static readonly IReadOnlyList<(int start, int end)> Gaps =
        new[] { (0, 1000) };

    [Theory]
    [InlineData(427, true, true)]   // 300 + 8 + 120
    [InlineData(500, true, true)]   // Activity fits, but the compact tile gives it more room.
    [InlineData(708, true, false)]  // 300 + 8 + 400
    [InlineData(299, false, true)]
    [InlineData(300, false, false)]
    public void ResetCredits_UsesTheMeasuredLaneAndRequestedActivity(int width, bool activity, bool hidden)
    {
        Assert.Equal(hidden, TaskbarWidgetPlacement.ShouldHideCodexResetCredits(
            300, 210, false, 1, 50, int.MinValue, activity, TaskbarWidgetOrder.QuotaFirst, [(50, 50 + width)]));
    }

    [Fact]
    public void ResetCredits_RestoresOnlyAfterStableHeadroomAtFractionalDpi()
    {
        bool hidden = false;
        foreach (var (width, expected) in new[] { (884, true), (885, true), (894, true), (895, false), (885, false) })
        {
            hidden = TaskbarWidgetPlacement.ShouldHideCodexResetCredits(
                300, 210, hidden, 1.25, 0, int.MinValue, true, TaskbarWidgetOrder.QuotaFirst, [(0, width)]);
            Assert.Equal(expected, hidden);
        }
    }

    [Fact]
    public void ResetCredits_LeavesManuallyDetachedActivityInItsOwnLane()
    {
        Assert.False(TaskbarWidgetPlacement.ShouldHideCodexResetCredits(
            300, 210, false, 1, 0, 610, true, TaskbarWidgetOrder.QuotaFirst, [(0, 330), (600, 800)]));
    }

    [Theory]
    [InlineData(false, 290)]
    [InlineData(true, 400)]
    public void ResetCredits_PreservesPreferredLaneEvenWithAWiderLaneAcrossTheDock(bool activity, int firstEnd)
    {
        Assert.True(TaskbarWidgetPlacement.ShouldHideCodexResetCredits(
            300, 210, false, 1, 0, int.MinValue, activity, TaskbarWidgetOrder.QuotaFirst,
            [(0, firstEnd), (700, 1600)]));
    }

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
    public void PlaceAdaptivePair_QuotaFirst_ShrinksActivityWithoutMovingQuota()
    {
        var result = TaskbarWidgetPlacement.PlaceAdaptivePair(
            preferredQuotaX: 100, quotaWidth: 440,
            minimumActivityWidth: 120, maximumActivityWidth: 400, gap: 8,
            TaskbarWidgetOrder.QuotaFirst, new[] { (0, 800) });

        Assert.Equal(new AdaptiveWidgetPairPlacement(100, 548, 252, 700), result);
    }

    [Fact]
    public void PlaceAdaptivePair_ActivityFirst_PreservesQuotaAndOrder()
    {
        var result = TaskbarWidgetPlacement.PlaceAdaptivePair(
            preferredQuotaX: 600, quotaWidth: 180,
            minimumActivityWidth: 120, maximumActivityWidth: 400, gap: 8,
            TaskbarWidgetOrder.ActivityFirst, new[] { (100, 1000) });

        Assert.Equal(new AdaptiveWidgetPairPlacement(600, 192, 400, 588), result);
    }

    [Fact]
    public void PlaceAdaptivePair_ChoosesNearestLaneThatFitsBothWidgets()
    {
        var result = TaskbarWidgetPlacement.PlaceAdaptivePair(
            preferredQuotaX: 300, quotaWidth: 180,
            minimumActivityWidth: 120, maximumActivityWidth: 400, gap: 8,
            TaskbarWidgetOrder.QuotaFirst, new[] { (0, 250), (500, 900) });

        Assert.Equal(new AdaptiveWidgetPairPlacement(500, 688, 212, 400), result);
    }

    [Fact]
    public void PlaceAdaptivePair_ReturnsNullWhenMinimumPairCannotFit()
    {
        var result = TaskbarWidgetPlacement.PlaceAdaptivePair(
            preferredQuotaX: 0, quotaWidth: 180,
            minimumActivityWidth: 120, maximumActivityWidth: 400, gap: 8,
            TaskbarWidgetOrder.QuotaFirst, new[] { (0, 300) });

        Assert.Null(result);
    }

    [Fact]
    public void PlaceAdaptivePair_PreservesDetachedActivityPosition()
    {
        var result = TaskbarWidgetPlacement.PlaceAdaptivePair(
            preferredQuotaX: 100, quotaWidth: 180,
            minimumActivityWidth: 120, maximumActivityWidth: 400, gap: 8,
            TaskbarWidgetOrder.QuotaFirst, new[] { (0, 1000) },
            preferredActivityX: 700, currentActivityWidth: 120);

        Assert.Equal(new AdaptiveWidgetPairPlacement(100, 700, 300, 488), result);
    }

    [Fact]
    public void PlaceAdaptivePair_ManualActivityFirstDropKeepsItsLeftEdge()
    {
        var result = TaskbarWidgetPlacement.PlaceAdaptivePair(
            preferredQuotaX: 800, quotaWidth: 180,
            minimumActivityWidth: 120, maximumActivityWidth: 400, gap: 8,
            TaskbarWidgetOrder.ActivityFirst, new[] { (0, 1000) },
            preferredActivityX: 200, currentActivityWidth: 120);

        Assert.Equal(new AdaptiveWidgetPairPlacement(800, 200, 400, 588), result);
    }

    [Fact]
    public void PlaceAdaptivePair_AutomaticPositionReturnsBesideNarrowerQuota()
    {
        var wideQuota = TaskbarWidgetPlacement.PlaceAdaptivePair(
            preferredQuotaX: 100, quotaWidth: 440,
            minimumActivityWidth: 120, maximumActivityWidth: 400, gap: 8,
            TaskbarWidgetOrder.QuotaFirst, new[] { (0, 800) });
        var narrowQuota = TaskbarWidgetPlacement.PlaceAdaptivePair(
            preferredQuotaX: 100, quotaWidth: 180,
            minimumActivityWidth: 120, maximumActivityWidth: 400, gap: 8,
            TaskbarWidgetOrder.QuotaFirst, new[] { (0, 800) });

        Assert.Equal(new AdaptiveWidgetPairPlacement(100, 548, 252, 700), wideQuota);
        Assert.Equal(new AdaptiveWidgetPairPlacement(100, 288, 400, 588), narrowQuota);
    }

    [Fact]
    public void OccupyDifferentGaps_DetectsOppositeTaskbarLanes()
    {
        var gaps = new[] { (0, 360), (620, 1200) };

        Assert.True(TaskbarWidgetPlacement.OccupyDifferentGaps(
            firstX: 100, firstWidth: 180,
            secondX: 700, secondWidth: 120,
            gaps));
    }

    [Fact]
    public void OccupyDifferentGaps_IsFalseWithinOneTaskbarLane()
    {
        var gaps = new[] { (0, 1000) };

        Assert.False(TaskbarWidgetPlacement.OccupyDifferentGaps(
            firstX: 100, firstWidth: 180,
            secondX: 700, secondWidth: 120,
            gaps));
    }

    [Theory]
    [InlineData(266, 272)]
    [InlineData(470, 488)]
    public void SnapNextToPartner_GluesActivityWhenDroppedClose(int droppedX, int expectedX)
    {
        var result = TaskbarWidgetPlacement.SnapNextToPartner(
            droppedX, draggedWidth: 120,
            partnerX: 400, partnerWidth: 80,
            gap: 8, maximumDistance: 32,
            new[] { (0, 392), (488, 1000) });

        Assert.Equal(expectedX, result);
    }

    [Fact]
    public void SnapNextToPartner_LeavesDetachedActivityAloneOutsideMagneticZone()
    {
        var result = TaskbarWidgetPlacement.SnapNextToPartner(
            droppedX: 200, draggedWidth: 120,
            partnerX: 400, partnerWidth: 80,
            gap: 8, maximumDistance: 32,
            new[] { (0, 392), (488, 1000) });

        Assert.Null(result);
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
