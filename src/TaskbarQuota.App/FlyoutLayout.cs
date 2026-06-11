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
        /// Chrome beyond provider icons: bottom margin, strip border padding, strip/settings gap,
        /// and the settings button.
        /// </summary>
        public const int StripChromeLogicalWidth = 88;

        /// <summary>Extra width reserved for the dashboard detail card inside the frame.</summary>
        public const int DetailContentPadding = 40;

        /// <summary>ContentFrame left + right padding in the flyout.</summary>
        public const int FrameHorizontalPadding = 32;

        /// <summary>Header + tallest provider detail + status (content width baseline).</summary>
        public const int LogicalContentHeight = 430;

        /// <summary>Frame padding + scroll padding + bottom chrome (update bar is optional / collapsed).</summary>
        public const int ChromeLogicalHeight = 122;

        public const int HeightMeasureBuffer = 4;

        public static int LogicalHeight =>
            LogicalContentHeight + ChromeLogicalHeight + HeightMeasureBuffer;

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