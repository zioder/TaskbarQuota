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
