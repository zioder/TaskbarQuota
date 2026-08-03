using System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Controls
{
    /// <summary>Renders provider SVG paths into a normalized <see cref="Path"/> (same approach as the taskbar widget).</summary>
    internal static class ProviderGlyphRenderer
    {
        private const double ViewportSize = 100;
        private const double NormalizedExtent = 88;

        public static bool TryApply(Path path, ProviderId providerId, Brush foreground)
        {
            if (!ProviderGlyphs.Data.TryGetValue(providerId, out var pathData)
                || Ui.ParseFreshGeometry(pathData) is not { } glyph)
            {
                return false;
            }

            path.Data = glyph;
            path.Fill = foreground;
            ApplyTransform(path, providerId);
            return true;
        }

        public static void ApplyTransform(Path path, ProviderId? providerId = null)
        {
            var bounds = path.Data?.Bounds ?? Rect.Empty;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.RenderTransform = null;
                return;
            }

            double scale = NormalizedExtent / Math.Max(bounds.Width, bounds.Height);
            double scaleX = scale * OpticalScaleX(providerId);
            path.RenderTransform = new CompositeTransform
            {
                ScaleX = scaleX,
                ScaleY = scale,
                TranslateX = (ViewportSize / 2) - ((bounds.X + bounds.Width / 2) * scaleX),
                TranslateY = (ViewportSize / 2) - ((bounds.Y + bounds.Height / 2) * scale),
            };
        }

        internal static double OpticalScaleX(ProviderId? providerId)
            => providerId is ProviderId.OpenCode or ProviderId.OpenCodeGo ? 1.24 : 1;

        public static double Viewport => ViewportSize;
    }
}
