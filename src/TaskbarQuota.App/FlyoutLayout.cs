namespace TaskbarQuota
{
    /// <summary>
    /// Pre-measured flyout dimensions (7 provider icons + settings).
    /// Tallest page: 3 usage bars with reset lines (Cursor / Codex / OpenCode Go).
    /// </summary>
    internal static class FlyoutLayout
    {
        /// <summary>7×48 icons + provider padding/border + 8 spacing + 48 settings + border + 12×2 margin.</summary>
        public const int LogicalWidth = 428;

        /// <summary>Header + tallest provider detail + status (content width 396).</summary>
        public const int LogicalContentHeight = 390;

        /// <summary>Frame padding + scroll padding + bottom chrome (update bar is optional / collapsed).</summary>
        public const int ChromeLogicalHeight = 122;

        public const int HeightMeasureBuffer = 4;

        public static int LogicalHeight =>
            LogicalContentHeight + ChromeLogicalHeight + HeightMeasureBuffer;
    }
}
