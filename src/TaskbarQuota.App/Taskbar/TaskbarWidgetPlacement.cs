using System;
using System.Collections.Generic;

namespace TaskbarQuota.Taskbar;

internal enum TaskbarWidgetRole
{
    Quota,
    Activity,
}

internal enum TaskbarWidgetOrder
{
    QuotaFirst,
    ActivityFirst,
}

internal readonly record struct WidgetPairPlacement(int QuotaX, int ActivityX, int Width);
internal readonly record struct AdaptiveWidgetPlacement(int X, int Width);
internal readonly record struct AdaptiveWidgetPairPlacement(
    int QuotaX,
    int ActivityX,
    int ActivityWidth,
    int Width);

internal static class TaskbarWidgetPlacement
{
    internal static AdaptiveWidgetPairPlacement? PlaceActivityBesideQuota(
        int quotaX, int quotaWidth, int manualActivityX, double scale, TaskbarWidgetOrder order,
        IReadOnlyList<(int start, int end)> gaps)
    {
        int minimumActivityWidth = (int)Math.Ceiling(Controls.AgentActivitySummary.MinimumLogicalWidth * scale);
        int maximumActivityWidth = (int)Math.Ceiling(Controls.AgentActivitySummary.DesiredLogicalWidth * scale);
        int separation = (int)Math.Ceiling(TaskBarWidget.ActivitySummaryMarginLogicalPx * scale);
        if (manualActivityX != int.MinValue
            && TaskBarWidget.PlaceInFittingGap(quotaX, gaps, quotaWidth) is { } detachedQuotaX)
        {
            var activity = PlaceAdaptive(manualActivityX, minimumActivityWidth, minimumActivityWidth,
                maximumActivityWidth, anchorRight: false, gaps);
            if (activity is { } detached && OccupyDifferentGaps(
                detachedQuotaX, quotaWidth, detached.X, detached.Width, gaps))
                return new(detachedQuotaX, detached.X, detached.Width, quotaWidth + detached.Width);
        }

        return PlaceAdaptivePair(quotaX, quotaWidth, minimumActivityWidth, maximumActivityWidth,
            separation, order, gaps, manualActivityX);
    }

    internal static bool CanFitQuotaRow(int rowWidth, int availableLogicalWidth,
        int quotaX, int manualActivityX, double scale, bool showActivity, TaskbarWidgetOrder order,
        IReadOnlyList<(int start, int end)>? gaps)
    {
        if (showActivity && gaps is not null)
        {
            int physicalWidth = (int)Math.Ceiling(rowWidth * scale);
            return PlaceActivityBesideQuota(quotaX, physicalWidth, manualActivityX, scale, order, gaps) is not null;
        }

        int reservedWidth = showActivity
            ? Controls.AgentActivitySummary.MinimumLogicalWidth + TaskBarWidget.ActivitySummaryMarginLogicalPx
            : 0;
        return rowWidth + reservedWidth <= availableLogicalWidth;
    }

    internal static bool ShouldHideCodexResetCredits(int fullRow, int compactRow, bool wasHidden,
        double scale, int quotaX, int manualActivityX, bool showActivity, TaskbarWidgetOrder order,
        IReadOnlyList<(int start, int end)> gaps)
    {
        if (compactRow >= fullRow)
            return false;

        // Khi đã ẩn, cần dư thêm 8 DIP để sai số đo hoặc biến động nhỏ của dock không làm hàng nhấp nháy.
        int fullWidth = (int)Math.Ceiling((fullRow + (wasHidden ? 8 : 0)) * scale);
        int compactWidth = (int)Math.Ceiling(compactRow * scale);
        bool CompactKeepsCloserLane(int fullX, int compactX) =>
            Math.Abs((long)compactX - quotaX) < Math.Abs((long)fullX - quotaX)
            && OccupyDifferentGaps(fullX, fullWidth, compactX, compactWidth, gaps);
        if (showActivity)
        {
            var full = PlaceActivityBesideQuota(quotaX, fullWidth, manualActivityX, scale, order, gaps);
            var compact = PlaceActivityBesideQuota(quotaX, compactWidth, manualActivityX, scale, order, gaps);
            if (compact is { } fit)
                return full is null || fit.ActivityWidth > full.Value.ActivityWidth
                    || CompactKeepsCloserLane(full.Value.QuotaX, fit.QuotaX);
            if (full is not null)
                return false;
        }

        int? fullX = TaskBarWidget.PlaceInFittingGap(quotaX, gaps, fullWidth);
        return TaskBarWidget.PlaceInFittingGap(quotaX, gaps, compactWidth) is { } compactX
            && (fullX is null || CompactKeepsCloserLane(fullX.Value, compactX));
    }

    internal static WidgetPairPlacement? PlacePair(
        int preferredAnchor,
        int quotaWidth,
        int activityWidth,
        int gap,
        TaskbarWidgetOrder order,
        IReadOnlyList<(int start, int end)> freeGaps)
    {
        int total = quotaWidth + activityWidth + gap;
        int? anchor = null;
        long bestDistance = long.MaxValue;
        foreach (var zone in freeGaps)
        {
            if (zone.end - zone.start < total)
                continue;
            int candidate = Math.Clamp(preferredAnchor, zone.start, zone.end - total);
            long distance = Math.Abs((long)candidate - preferredAnchor);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                anchor = candidate;
            }
        }

        if (anchor is not { } x)
            return null;

        return order == TaskbarWidgetOrder.QuotaFirst
            ? new WidgetPairPlacement(x, x + quotaWidth + gap, total)
            : new WidgetPairPlacement(x + activityWidth + gap, x, total);
    }

    internal static AdaptiveWidgetPlacement? PlaceAdaptive(
        int preferredX,
        int currentWidth,
        int minimumWidth,
        int maximumWidth,
        bool anchorRight,
        IReadOnlyList<(int start, int end)> freeGaps)
    {
        int preferredEdge = anchorRight ? preferredX + currentWidth : preferredX;
        AdaptiveWidgetPlacement? best = null;
        long bestDistance = long.MaxValue;

        foreach (var zone in freeGaps)
        {
            int laneWidth = zone.end - zone.start;
            if (laneWidth < minimumWidth)
                continue;

            int width = Math.Min(maximumWidth, laneWidth);
            int desiredX = anchorRight ? preferredEdge - width : preferredEdge;
            int x = Math.Clamp(desiredX, zone.start, zone.end - width);
            int placedEdge = anchorRight ? x + width : x;
            long distance = Math.Abs((long)placedEdge - preferredEdge);
            if (distance < bestDistance
                || (distance == bestDistance && (best is null || width > best.Value.Width)))
            {
                bestDistance = distance;
                best = new AdaptiveWidgetPlacement(x, width);
            }
        }

        return best;
    }

    /// <summary>
    /// Places quota and activity as one unit while allowing only the activity surface to shrink.
    /// The quota stays at its preferred position whenever the lane permits, which prevents a quota
    /// width change from using the activity window's stale bounds and making the two islands jump.
    /// </summary>
    internal static AdaptiveWidgetPairPlacement? PlaceAdaptivePair(
        int preferredQuotaX,
        int quotaWidth,
        int minimumActivityWidth,
        int maximumActivityWidth,
        int gap,
        TaskbarWidgetOrder order,
        IReadOnlyList<(int start, int end)> freeGaps,
        int preferredActivityX = int.MinValue,
        int currentActivityWidth = 0)
    {
        AdaptiveWidgetPairPlacement? best = null;
        long bestDistance = long.MaxValue;

        foreach (var zone in freeGaps)
        {
            int laneWidth = zone.end - zone.start;
            int fixedWidth = quotaWidth + gap;
            if (laneWidth < fixedWidth + minimumActivityWidth)
                continue;

            int quotaX;
            int activityX;
            int activityWidth;
            if (order == TaskbarWidgetOrder.QuotaFirst)
            {
                quotaX = Math.Clamp(
                    preferredQuotaX,
                    zone.start,
                    zone.end - fixedWidth - minimumActivityWidth);
                int defaultActivityX = quotaX + fixedWidth;
                activityX = preferredActivityX == int.MinValue
                    ? defaultActivityX
                    : Math.Clamp(preferredActivityX, defaultActivityX, zone.end - minimumActivityWidth);
                activityWidth = Math.Min(maximumActivityWidth, zone.end - activityX);
            }
            else
            {
                quotaX = Math.Clamp(
                    preferredQuotaX,
                    zone.start + gap + minimumActivityWidth,
                    zone.end - quotaWidth);
                int defaultActivityRight = quotaX - gap;
                if (preferredActivityX == int.MinValue)
                {
                    activityWidth = Math.Min(maximumActivityWidth, defaultActivityRight - zone.start);
                    activityX = defaultActivityRight - activityWidth;
                }
                else
                {
                    // A manual drop owns its left edge. Grow into the free space on its right instead of
                    // preserving the compact drag width's right edge and pulling the window toward zero.
                    activityX = Math.Clamp(
                        preferredActivityX,
                        zone.start,
                        defaultActivityRight - minimumActivityWidth);
                    activityWidth = Math.Min(maximumActivityWidth, defaultActivityRight - activityX);
                }
            }

            int totalWidth = fixedWidth + activityWidth;
            long distance = Math.Abs((long)quotaX - preferredQuotaX);

            if (distance < bestDistance
                || (distance == bestDistance
                    && (best is null || activityWidth > best.Value.ActivityWidth)))
            {
                bestDistance = distance;
                best = new AdaptiveWidgetPairPlacement(quotaX, activityX, activityWidth, totalWidth);
            }
        }

        return best;
    }

    internal static bool OccupyDifferentGaps(
        int firstX,
        int firstWidth,
        int secondX,
        int secondWidth,
        IReadOnlyList<(int start, int end)> freeGaps)
    {
        int firstGap = ContainingGap(firstX, firstWidth, freeGaps);
        int secondGap = ContainingGap(secondX, secondWidth, freeGaps);
        return firstGap >= 0 && secondGap >= 0 && firstGap != secondGap;
    }

    internal static int? SnapNextToPartner(
        int droppedX,
        int draggedWidth,
        int partnerX,
        int partnerWidth,
        int gap,
        int maximumDistance,
        IReadOnlyList<(int start, int end)> fittingGaps)
    {
        int leftCandidate = partnerX - gap - draggedWidth;
        int rightCandidate = partnerX + partnerWidth + gap;
        int? best = null;
        long bestDistance = long.MaxValue;

        foreach (int candidate in new[] { leftCandidate, rightCandidate })
        {
            long distance = Math.Abs((long)candidate - droppedX);
            if (distance > maximumDistance
                || ContainingGap(candidate, draggedWidth, fittingGaps) < 0
                || distance >= bestDistance)
            {
                continue;
            }

            best = candidate;
            bestDistance = distance;
        }

        return best;
    }

    private static int ContainingGap(
        int x,
        int width,
        IReadOnlyList<(int start, int end)> freeGaps)
    {
        for (int i = 0; i < freeGaps.Count; i++)
        {
            var gap = freeGaps[i];
            if (x >= gap.start && x + width <= gap.end)
                return i;
        }

        return -1;
    }

    internal static TaskbarWidgetOrder OrderForDraggedWidget(
        TaskbarWidgetRole dragged,
        int draggedX,
        int draggedWidth,
        int partnerX,
        int partnerWidth,
        TaskbarWidgetOrder current,
        int hysteresis = 0)
    {
        int draggedCenter = draggedX + draggedWidth / 2;
        int partnerCenter = partnerX + partnerWidth / 2;
        if (Math.Abs((long)draggedCenter - partnerCenter) <= hysteresis)
            return current;

        if (dragged == TaskbarWidgetRole.Activity)
            return draggedCenter < partnerCenter ? TaskbarWidgetOrder.ActivityFirst : TaskbarWidgetOrder.QuotaFirst;

        return draggedCenter < partnerCenter ? TaskbarWidgetOrder.QuotaFirst : TaskbarWidgetOrder.ActivityFirst;
    }

    internal static int StepAnimatedPosition(int current, int target)
    {
        int distance = target - current;
        if (Math.Abs((long)distance) <= 1)
            return target;

        int step = (int)Math.Round(distance * 0.32, MidpointRounding.AwayFromZero);
        if (step == 0)
            step = Math.Sign(distance);
        return current + step;
    }
}
