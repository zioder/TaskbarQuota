using Windows.Graphics;

namespace TaskbarQuota
{
    internal static class WindowDpi
    {
        public static int ToPhysical(double logicalValue, double rasterizationScale)
            => (int)System.Math.Round(logicalValue * rasterizationScale);

        public static SizeInt32 ToPhysicalSize(double logicalWidth, double logicalHeight, double rasterizationScale)
            => new(ToPhysical(logicalWidth, rasterizationScale), ToPhysical(logicalHeight, rasterizationScale));
    }
}
