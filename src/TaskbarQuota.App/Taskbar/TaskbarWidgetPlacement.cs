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
