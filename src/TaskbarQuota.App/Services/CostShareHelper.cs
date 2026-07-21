using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;

namespace TaskbarQuota.Services
{
    /// <summary>
    /// Renders a XAML element to a PNG and hands it to the Windows Share sheet, so the user can post
    /// their cost card to any share target (messaging, social, mail) without leaving the app. Falls
    /// back to placing the image on the clipboard when no window handle is available for the sheet.
    /// </summary>
    public static class CostShareHelper
    {
        public static async Task ShareAsync(FrameworkElement element, IntPtr windowHandle, string baseName)
        {
            StorageFile file = await RenderToPngAsync(element, baseName);

            if (windowHandle == IntPtr.Zero)
            {
                CopyToClipboard(file);
                return;
            }

            DataTransferManager dtm = GetForWindow(windowHandle);
            TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = (_, e) =>
            {
                var req = e.Request.Data;
                req.Properties.Title = "AI usage cost";
                req.Properties.Description = "API-equivalent cost of tokens used";
                req.SetStorageItems(new[] { file });
                req.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                dtm.DataRequested -= handler;
            };
            dtm.DataRequested += handler;

            ShowShareUIForWindow(windowHandle);
        }

        private static async Task<StorageFile> RenderToPngAsync(FrameworkElement element, string baseName)
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(element);

            var pixels = await rtb.GetPixelsAsync();
            double scale = element.XamlRoot?.RasterizationScale ?? 1.0;

            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(System.IO.Path.GetTempPath());
            var file = await folder.CreateFileAsync($"{baseName}.png", CreationCollisionOption.ReplaceExisting);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);

            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth,
                (uint)rtb.PixelHeight,
                96 * scale,
                96 * scale,
                pixels.ToArray());
            await encoder.FlushAsync();

            return file;
        }

        private static void CopyToClipboard(StorageFile file)
        {
            var pkg = new DataPackage();
            pkg.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            pkg.SetStorageItems(new[] { file });
            Clipboard.SetContent(pkg);
        }

        // --- Per-window Share sheet interop (WinUI 3 has no window-less DataTransferManager) ---

        private static readonly Guid DtmIid =
            new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

        private static DataTransferManager GetForWindow(IntPtr hwnd)
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            Guid iid = DtmIid;
            IntPtr abi = interop.GetForWindow(hwnd, ref iid);
            return MarshalInterface<DataTransferManager>.FromAbi(abi);
        }

        private static void ShowShareUIForWindow(IntPtr hwnd) =>
            DataTransferManager.As<IDataTransferManagerInterop>().ShowShareUIForWindow(hwnd);

        [ComImport]
        [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDataTransferManagerInterop
        {
            IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
            void ShowShareUIForWindow([In] IntPtr appWindow);
        }
    }
}
