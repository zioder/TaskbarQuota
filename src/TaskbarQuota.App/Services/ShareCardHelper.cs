using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TaskbarQuota.Services
{
    /// <summary>
    /// Renders a UI element to a PNG image on the clipboard as a share card, and shows a short-lived
    /// "Copied" confirmation on a TeachingTip.
    /// </summary>
    public static class ShareCardHelper
    {
        private static readonly TimeSpan TipDuration = TimeSpan.FromMilliseconds(1600);
        // RenderTargetBitmap caps out around 4096px per dimension; stay below that.
        private const int MaxRenderPixels = 4096;

        /// <summary>
        /// Captures <paramref name="element"/> as rendered on screen and copies it to the clipboard as
        /// a PNG bitmap. Must be called on the UI thread.
        /// </summary>
        public static async Task<bool> CopyElementToClipboardAsync(FrameworkElement element)
        {
            try
            {
                // Render above 96 DPI so the pasted image stays crisp (text especially).
                var scale = GetRenderScale(element);
                var renderWidth = (int)Math.Ceiling(element.ActualWidth * scale);
                var renderHeight = (int)Math.Ceiling(element.ActualHeight * scale);
                if (renderWidth <= 0 || renderHeight <= 0)
                    return false;

                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(element, renderWidth, renderHeight);

                uint width = (uint)bitmap.PixelWidth;
                uint height = (uint)bitmap.PixelHeight;
                if (width == 0 || height == 0)
                    return false;

                var pixels = await bitmap.GetPixelsAsync();
                var bytes = new byte[pixels.Length];
                using (var reader = DataReader.FromBuffer(pixels))
                {
                    reader.ReadBytes(bytes);
                }

                // RenderTargetBitmap produces a transparent canvas. Dark-theme card backgrounds are
                // nearly transparent white and dark-theme text is white, so pasting the raw pixels
                // into apps that flatten transparency onto white yields a blank-looking image.
                // Composite over an opaque, theme-appropriate background before encoding.
                CompositeOverBackground(bytes, GetBackgroundColor(element));

                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    width,
                    height,
                    96 * scale,
                    96 * scale,
                    bytes);
                await encoder.FlushAsync();

                var dataPackage = new DataPackage();
                dataPackage.RequestedOperation = DataPackageOperation.Copy;
                dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double GetRenderScale(FrameworkElement element)
        {
            var scale = 2.0 * (element.XamlRoot?.RasterizationScale ?? 1.0);
            if (element.ActualWidth > 0)
                scale = Math.Min(scale, MaxRenderPixels / element.ActualWidth);
            if (element.ActualHeight > 0)
                scale = Math.Min(scale, MaxRenderPixels / element.ActualHeight);
            return Math.Max(scale, 1.0);
        }

        internal static Windows.UI.Color GetBackgroundColor(FrameworkElement element)
            => element.ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                : Windows.UI.Color.FromArgb(255, 255, 255, 255);

        /// <summary>
        /// Flattens premultiplied BGRA pixels onto an opaque background color, in place.
        /// out = src + background * (1 - srcAlpha).
        /// </summary>
        internal static void CompositeOverBackground(byte[] pixels, Windows.UI.Color background)
        {
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                if (alpha == 255)
                    continue;

                var inverse = 255 - alpha;
                pixels[i + 0] = (byte)Math.Min(255, pixels[i + 0] + background.B * inverse / 255);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + background.G * inverse / 255);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + background.R * inverse / 255);
                pixels[i + 3] = 255;
            }
        }

        /// <summary>
        /// Opens <paramref name="tip"/> anchored to <paramref name="target"/> and auto-dismisses it after
        /// a short delay, mirroring a transient "Copied to clipboard" confirmation.
        /// </summary>
        public static void ShowTransientTip(TeachingTip tip, string title, FrameworkElement target)
        {
            if (tip is null || target is null)
                return;

            var dispatcher = target.DispatcherQueue;
            tip.Target = target;
            tip.Title = title;
            tip.IsOpen = true;

            var timer = dispatcher.CreateTimer();
            timer.Interval = TipDuration;
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                tip.IsOpen = false;
            };
            timer.Start();
        }
    }
}
