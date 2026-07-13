using System;

namespace TaskbarQuota
{
    /// <summary>
    /// Flyout dimensions. Width adapts to the number of visible provider icons and detail content.
    /// </summary>
    internal static class FlyoutLayout
    {
        public const int IconButtonWidth = 48;
        public const int CompactLogicalWidth = 420;
        public const int CompactProviderLogicalHeight = 104;
        public const int CompactChromeLogicalHeight = 88;
        public const int CompactMinLogicalHeight = 260;
        public const int CompactMaxLogicalHeight = 720;

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

        /// <summary>
        /// Stable compact dashboard content height used by the tray flyout. Taller provider detail
        /// panes scroll inside this frame instead of resizing the native tray window on selection.
        /// </summary>
        public const int FixedLogicalContentHeight = 620;

        /// <summary>Largest dashboard content height before scrolling takes over.</summary>
        public const int MaxLogicalContentHeight = 760;

        /// <summary>Frame padding + scroll padding + bottom chrome (update bar is optional / collapsed).</summary>
        public const int ChromeLogicalHeight = 122;

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

        public static int ComputeCompactLogicalHeight(int providerCount)
            => Math.Clamp(
                Math.Max(0, providerCount) * CompactProviderLogicalHeight + CompactChromeLogicalHeight,
                CompactMinLogicalHeight,
                CompactMaxLogicalHeight);

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
    }
}
