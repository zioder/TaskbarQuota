using System.Collections.Generic;
using TaskbarQuota.Interop;
using TaskbarQuota.Taskbar;

namespace TaskbarQuota.Tests;

public class TaskBarWidgetGapTests
{
    private static RECT R(int left, int right) => new() { left = left, top = 0, right = right, bottom = 40 };

    [Fact]
    public void ComputeFreeGaps_NoObstacles_ReturnsWholeSpan()
    {
        var gaps = TaskBarWidget.ComputeFreeGaps(0, 1000, new List<RECT>());

        Assert.Single(gaps);
        Assert.Equal((0, 1000), gaps[0]);
    }

    [Fact]
    public void ComputeFreeGaps_ObstacleInMiddle_SplitsIntoTwoGaps()
    {
        var gaps = TaskBarWidget.ComputeFreeGaps(0, 1000, new List<RECT> { R(400, 600) });

        Assert.Equal(2, gaps.Count);
        Assert.Equal((0, 400), gaps[0]);
        Assert.Equal((600, 1000), gaps[1]);
    }

    [Fact]
    public void ComputeFreeGaps_OverlappingObstacles_AreMerged()
    {
        var gaps = TaskBarWidget.ComputeFreeGaps(0, 1000, new List<RECT> { R(400, 600), R(550, 700) });

        Assert.Equal(2, gaps.Count);
        Assert.Equal((0, 400), gaps[0]);
        Assert.Equal((700, 1000), gaps[1]);
    }

    [Fact]
    public void ComputeFreeGaps_ObstaclesClippedToBounds()
    {
        // Weather pill at the far left and tray at the far right, partly outside [100, 900].
        var gaps = TaskBarWidget.ComputeFreeGaps(100, 900, new List<RECT> { R(-50, 160), R(850, 1200) });

        Assert.Single(gaps);
        Assert.Equal((160, 850), gaps[0]);
    }

    [Fact]
    public void ComputeFreeGaps_EmptyWhenBoundsInverted()
    {
        Assert.Empty(TaskBarWidget.ComputeFreeGaps(500, 400, new List<RECT>()));
    }

    [Fact]
    public void PlaceInFittingGap_PreferredInsideFittingGap_KeepsPreferred()
    {
        var gaps = new List<(int, int)> { (0, 400), (600, 1000) };

        Assert.Equal(650, TaskBarWidget.PlaceInFittingGap(650, gaps, 172));
    }

    [Fact]
    public void PlaceInFittingGap_PreferredOnObstacle_SnapsToNearestFittingGap()
    {
        // Preferred 500 falls in the blocked band; the right gap [600,1000] is nearest and fits.
        var gaps = new List<(int, int)> { (0, 400), (600, 1000) };

        Assert.Equal(600, TaskBarWidget.PlaceInFittingGap(500, gaps, 172));
    }

    [Fact]
    public void PlaceInFittingGap_PreferredPastGapEnd_ClampsInsideGap()
    {
        var gaps = new List<(int, int)> { (600, 1000) };

        // width 172 -> max start is 1000-172 = 828.
        Assert.Equal(828, TaskBarWidget.PlaceInFittingGap(950, gaps, 172));
    }

    [Fact]
    public void PlaceInFittingGap_NoGapWideEnough_ReturnsNull()
    {
        var gaps = new List<(int, int)> { (0, 100), (600, 700) };

        Assert.Null(TaskBarWidget.PlaceInFittingGap(50, gaps, 172));
    }

    // The drag itself is unconstrained (issue #21): it only clamps to the span, so it can never stall or
    // skip a zone. Snapping to a free gap happens once, on release.
    [Fact]
    public void ClampToSpan_InsideSpan_KeepsRequestedPosition()
    {
        Assert.Equal(500, TaskBarWidget.ClampToSpan(500, 0, 1000, 172));
    }

    [Fact]
    public void ClampToSpan_PastRightEdge_StopsAtSpanEnd()
    {
        Assert.Equal(828, TaskBarWidget.ClampToSpan(9999, 0, 1000, 172));
    }

    [Fact]
    public void ClampToSpan_BeforeLeftEdge_StopsAtSpanStart()
    {
        Assert.Equal(100, TaskBarWidget.ClampToSpan(-50, 100, 1000, 172));
    }

    [Fact]
    public void ClampToSpan_SpanNarrowerThanWidget_PinsToStartInsteadOfRefusing()
    {
        Assert.Equal(100, TaskBarWidget.ClampToSpan(400, 100, 200, 172));
    }

    // Centred taskbar: icon cluster at [400,600] leaves a left zone and a right zone.
    private static readonly List<(int start, int end)> CentredLayout = new() { (0, 400), (600, 1000) };

    [Fact]
    public void SelectDragGap_CursorInAZone_TracksThatZone()
    {
        var gap = TaskBarWidget.SelectDragGap(CentredLayout, cursorX: 200, desiredX: 150, width: 172, current: null);

        Assert.Equal((0, 400), gap);
    }

    [Fact]
    public void SelectDragGap_CursorCrossesTheIconCluster_KeepsTheZoneItStartedIn()
    {
        // Cursor is over the icons at x=500 — no zone contains it. The widget must wait in the left zone,
        // not teleport to the right one (the random lane-jumping in issue #21).
        var gap = TaskBarWidget.SelectDragGap(CentredLayout, cursorX: 500, desiredX: 450, width: 172, current: (0, 400));

        Assert.Equal((0, 400), gap);
    }

    [Fact]
    public void SelectDragGap_CursorReachesTheFarSide_HandsOverToThatZone()
    {
        var gap = TaskBarWidget.SelectDragGap(CentredLayout, cursorX: 700, desiredX: 650, width: 172, current: (0, 400));

        Assert.Equal((600, 1000), gap);
    }

    [Fact]
    public void SelectDragGap_StickyZoneSurvivesObstacleRectsShiftingByAPixel()
    {
        var relaidOut = new List<(int start, int end)> { (0, 398), (602, 1000) };

        var gap = TaskBarWidget.SelectDragGap(relaidOut, cursorX: 500, desiredX: 450, width: 172, current: (0, 400));

        Assert.Equal((0, 398), gap);
    }

    [Fact]
    public void SelectDragGap_IgnoresZonesTooNarrowForTheWidget()
    {
        var gaps = new List<(int, int)> { (0, 100), (600, 1000) };

        // Cursor sits inside the narrow zone, which cannot hold the widget: fall through to the one that can.
        var gap = TaskBarWidget.SelectDragGap(gaps, cursorX: 50, desiredX: 20, width: 172, current: null);

        Assert.Equal((600, 1000), gap);
    }

    [Fact]
    public void SelectDragGap_NoZoneFits_ReturnsNull()
    {
        var gaps = new List<(int, int)> { (0, 100), (600, 700) };

        Assert.Null(TaskBarWidget.SelectDragGap(gaps, cursorX: 50, desiredX: 20, width: 172, current: null));
    }

    [Fact]
    public void FilterContainerRects_DropsRectsSpanningMoreThanHalfTheTaskbar()
    {
        // A UIA grouping element covering most of the bar would otherwise erase every free gap.
        var kept = TaskBarWidget.FilterContainerRects(new List<RECT> { R(0, 900), R(400, 460) }, taskbarWidth: 1000);

        Assert.Single(kept);
        Assert.Equal(400, kept[0].left);
    }

    [Fact]
    public void FilterContainerRects_KeepsEverythingWhenTaskbarWidthUnknown()
    {
        var rects = new List<RECT> { R(0, 900) };

        Assert.Same(rects, TaskBarWidget.FilterContainerRects(rects, taskbarWidth: 0));
    }

    [Fact]
    public void PlaceInFittingGap_SkipsTooSmallGap_PicksWiderOne()
    {
        // Nearest gap [400,500] is too small; must skip to the wide gap even though it's farther.
        var gaps = new List<(int, int)> { (400, 500), (600, 1000) };

        Assert.Equal(600, TaskBarWidget.PlaceInFittingGap(450, gaps, 172));
    }

    [Fact]
    public void ComputeUsableHorizontalBounds_PrimaryTaskbar_StopsBeforeTray()
    {
        var taskbar = R(0, 1920);
        var tray = R(1600, 1920);

        var bounds = TaskBarWidget.ComputeUsableHorizontalBounds(taskbar, tray, clearance: 6, isRtl: false);

        Assert.Equal((0, 1594), bounds);
    }

    [Fact]
    public void ComputeUsableHorizontalBounds_SecondaryTaskbar_UsesFreeOuterEdge()
    {
        var taskbar = R(0, 1920);

        var bounds = TaskBarWidget.ComputeUsableHorizontalBounds(taskbar, notificationRect: null, clearance: 6, isRtl: false);

        Assert.Equal((0, 1914), bounds);
        Assert.Equal(1742, TaskBarWidget.PlaceInFittingGap(
            preferredX: bounds.right - 172,
            TaskBarWidget.ComputeFreeGaps(bounds.left, bounds.right, new List<RECT>()),
            width: 172));
    }

    [Fact]
    public void ComputeUsableHorizontalBounds_RtlSecondaryTaskbar_LeavesClearanceAtLeftEdge()
    {
        var bounds = TaskBarWidget.ComputeUsableHorizontalBounds(
            R(0, 1920),
            notificationRect: null,
            clearance: 6,
            isRtl: true);

        Assert.Equal((6, 1920), bounds);
    }
}
