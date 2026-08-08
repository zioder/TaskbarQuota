using System;

namespace TaskbarQuota
{
    /// <summary>
    /// Flyout dimensions. Width adapts to the number of visible provider icons and detail content.
    /// </summary>
    internal static class FlyoutLayout
    {
        public const int IconButtonWidth = 48;

        /// <summary>
        /// Fixed default flyout width. The flyout stays exactly this wide no matter how many
        /// providers are installed; the provider strip only pushes it wider once the icons (or the
        /// detail card) genuinely need more room than this. Chosen wide enough that the dashboard
        /// content (session / weekly rows, reset dates) does not wrap at the default provider count.
        /// </summary>
        public const int BaseLogicalWidth = 450;

        /// <summary>Absolute floor; kept for callers that reference a minimum.</summary>
        public const int MinLogicalWidth = BaseLogicalWidth;

        /// <summary>
        /// Chrome beyond the provider icons, so the strip + activity + settings buttons fit without
        /// clipping the settings gear: bottom-chrome margins (12+12) + strip border &amp; padding
        /// (1+4+4+1) + two gaps (8+8) + two utility buttons (1+48+1 each) = 150.
        /// </summary>
        public const int StripChromeLogicalWidth = 150;

        /// <summary>Extra width reserved for the dashboard detail card inside the frame.</summary>
        public const int DetailContentPadding = 40;

        /// <summary>ContentFrame left + right padding in the flyout.</summary>
        public const int FrameHorizontalPadding = 32;

        /// <summary>Smallest dashboard content height before chrome is added.</summary>
        public const int MinLogicalContentHeight = 320;

        /// <summary>
        /// Stable compact dashboard content height used by the tray flyout. Taller provider detail
        /// panes scroll inside this frame instead of resizing the native tray window on selection.
        /// </summary>
        public const int FixedLogicalContentHeight = 620;

        /// <summary>Largest dashboard content height before scrolling takes over.</summary>
        public const int MaxLogicalContentHeight = 760;

        /// <summary>
        /// Frame padding + scroll padding + bottom chrome (update bar is optional / collapsed).
        /// Includes the quick surface/opacity bar above the provider strip (~52px).
        /// </summary>
        public const int ChromeLogicalHeight = 174;

        public const int HeightMeasureBuffer = 40;
        public const string ForceMinWidthEnvironmentVariable = "TASKBARQUOTA_FORCE_MIN_FLYOUT_WIDTH";

        public static int LogicalHeight =>
            ComputeLogicalHeight(MinLogicalContentHeight);

        public static int ComputeLogicalHeight(double detailContentHeight)
        {
            int contentHeight = (int)Math.Ceiling(detailContentHeight);
            contentHeight = Math.Clamp(contentHeight, MinLogicalContentHeight, MaxLogicalContentHeight);
            return contentHeight + ChromeLogicalHeight + HeightMeasureBuffer;
        }

        /// <summary>
        /// Flyout stays at <see cref="BaseLogicalWidth"/> and only grows past it when the provider
        /// strip or the measured detail card actually needs more room.
        /// </summary>
        public static int ComputeLogicalWidth(int stripIconCount, double detailContentWidth)
        {
            if (IsForceMinWidthEnabled())
                return BaseLogicalWidth;

            int icons = Math.Max(0, stripIconCount);
            int stripWidth = (icons * IconButtonWidth) + StripChromeLogicalWidth;
            int contentWidth = (int)Math.Ceiling(detailContentWidth + DetailContentPadding);
            return Math.Max(Math.Max(stripWidth, contentWidth), BaseLogicalWidth);
        }

        private static bool IsForceMinWidthEnabled()
        {
            var value = Environment.GetEnvironmentVariable(ForceMinWidthEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Positions a flyout relative to an anchor (taskbar widget or floating window), preferring
        /// above when there is room and flipping below when the top of the work area would crush height.
        /// All coordinates are physical pixels.
        /// </summary>
        public static FlyoutPlacementResult ComputePlacement(
            int anchorLeft,
            int anchorTop,
            int anchorRight,
            int anchorBottom,
            int workLeft,
            int workTop,
            int workRight,
            int workBottom,
            int width,
            int height,
            int gap)
        {
            int workWidth = Math.Max(0, workRight - workLeft);
            int workHeight = Math.Max(0, workBottom - workTop);
            width = Math.Clamp(width, 1, Math.Max(1, workWidth));
            height = Math.Clamp(height, 1, Math.Max(1, workHeight));
            gap = Math.Max(0, gap);

            int spaceAbove = Math.Max(0, anchorTop - workTop - gap);
            int spaceBelow = Math.Max(0, workBottom - anchorBottom - gap);

            bool placeAbove;
            if (spaceAbove >= height)
                placeAbove = true;
            else if (spaceBelow >= height)
                placeAbove = false;
            else
                placeAbove = spaceAbove >= spaceBelow;

            int available = placeAbove ? spaceAbove : spaceBelow;
            // Only shrink when neither side can host the full height; keep as much as fits.
            if (available < height)
                height = Math.Max(1, available);

            int x = Math.Clamp(anchorRight - width, workLeft, Math.Max(workLeft, workRight - width));
            int y = placeAbove
                ? anchorTop - height - gap
                : anchorBottom + gap;
            y = Math.Clamp(y, workTop, Math.Max(workTop, workBottom - height));

            return new FlyoutPlacementResult(x, y, width, height, placeAbove);
        }
    }

    /// <summary>Result of <see cref="FlyoutLayout.ComputePlacement"/> in physical pixels.</summary>
    internal readonly record struct FlyoutPlacementResult(
        int X,
        int Y,
        int Width,
        int Height,
        bool PlacedAbove);
}
