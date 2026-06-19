using System;

namespace TaskbarQuota
{
    /// <summary>
    /// Flyout dimensions. Width adapts to the number of visible provider icons and detail content.
    /// </summary>
    internal static class FlyoutLayout
    {
        public const int IconButtonWidth = 48;

        /// <summary>Minimum flyout width (roughly two providers + chrome).</summary>
        public const int MinLogicalWidth = 320;

        /// <summary>
        /// Chrome beyond the provider icons, so the strip + settings button fit without clipping the
        /// gear: bottom-chrome margins (12+12) + strip border &amp; padding (1+4+4+1) + strip/settings
        /// gap (8) + settings border &amp; button (1+48+1) = 92, plus a few px of slack for rounding.
        /// </summary>
        public const int StripChromeLogicalWidth = 100;

        /// <summary>Extra width reserved for the dashboard detail card inside the frame.</summary>
        public const int DetailContentPadding = 40;

        /// <summary>ContentFrame left + right padding in the flyout.</summary>
        public const int FrameHorizontalPadding = 32;

        /// <summary>Smallest dashboard content height before chrome is added.</summary>
        public const int MinLogicalContentHeight = 320;

        /// <summary>Largest dashboard content height before scrolling takes over.</summary>
        public const int MaxLogicalContentHeight = 760;

        /// <summary>Frame padding + scroll padding + bottom chrome (update bar is optional / collapsed).</summary>
        public const int ChromeLogicalHeight = 122;

        public const int HeightMeasureBuffer = 40;

        public static int LogicalHeight =>
            ComputeLogicalHeight(MinLogicalContentHeight);

        public static int ComputeLogicalHeight(double detailContentHeight)
        {
            int contentHeight = (int)Math.Ceiling(detailContentHeight);
            contentHeight = Math.Clamp(contentHeight, MinLogicalContentHeight, MaxLogicalContentHeight);
            return contentHeight + ChromeLogicalHeight + HeightMeasureBuffer;
        }

        /// <summary>
        /// Flyout width grows with each visible provider icon and the measured detail card width.
        /// </summary>
        public static int ComputeLogicalWidth(int stripIconCount, double detailContentWidth)
        {
            int icons = Math.Max(0, stripIconCount);
            int stripWidth = (icons * IconButtonWidth) + StripChromeLogicalWidth;
            int contentWidth = (int)Math.Ceiling(detailContentWidth + DetailContentPadding);
            return Math.Max(Math.Max(stripWidth, contentWidth), MinLogicalWidth);
        }
    }
}
