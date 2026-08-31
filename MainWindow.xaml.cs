using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace KeImageEditor
{
    public sealed partial class MainWindow : Window
    {

        byte[]? _src8;
        byte[]? _mask8;
        byte[]? _dst8;
        int _width;
        int _height;

        public MainWindow()
        {
            this.InitializeComponent();

            // WinUI 3 の Window は Width/Height を持たないため AppWindow を使う
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            appWindow.Resize(new Windows.Graphics.SizeInt32(720, 610));
        }

        // HWND
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr GetWindowHandle()
        {
            if (_hwnd == IntPtr.Zero)
                _hwnd = WindowNative.GetWindowHandle(this);
            return _hwnd;
        }

        // 共通：WriteableBitmap生成
        private WriteableBitmap CreateBitmapFromBytes(byte[] pixelsRgba)
        {
            // 表示用に BGRA + Premultiplied に変換
            var pixelsBgra = ToBgraPremultiplied(pixelsRgba);

            var wb = new WriteableBitmap(_width, _height);
            using (var s = wb.PixelBuffer.AsStream())
                s.Write(pixelsBgra, 0, pixelsBgra.Length);

            return wb;
        }


        // 元画像読み込み（クリック）
        private async Task OpenBaseImageAsync()
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            InitializeWithWindow.Initialize(picker, GetWindowHandle());

            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            await OpenBaseImageAsync(file);
        }

        // 元画像読み込み（StorageFile版）
        private async Task OpenBaseImageAsync(StorageFile file)
        {
            // ★ 基画像変更時にマスクをリセット
            _mask8 = null;
            MaskThumbnail.Source = null;
            MaskHintLabel.Visibility = Visibility.Visible;

            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            _width = (int)decoder.PixelWidth;
            _height = (int)decoder.PixelHeight;

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            _src8 = pixelData.DetachPixelData();
            _dst8 = new byte[_src8.Length];

            // サムネイル表示＋ラベル非表示
            BaseThumbnail.Source = CreateBitmapFromBytes(_src8);
            BaseHintLabel.Visibility = Visibility.Collapsed;
            //BaseLabel.Visibility = Visibility.Collapsed;

            // マスクがあれば自動合成
            if (_mask8 != null)
            {
                ProcessImageWithMask(_src8, _mask8, _dst8, _width, _height);
                ShowImage(_dst8);
            }
            else
            {
                // とりあえず元画像を出力側にも表示
                ShowImage(_src8);
            }
        }

        // マスク画像読み込み（クリック）
        private async Task OpenMaskImageAsync()
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            InitializeWithWindow.Initialize(picker, GetWindowHandle());

            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            await OpenMaskImageAsync(file);
        }

        // マスク画像読み込み（StorageFile版）★ここが今回のエラー原因だったので明示的に定義
        private async Task OpenMaskImageAsync(StorageFile file)
        {
            if (_src8 == null)
            {
                var dlg = new ContentDialog
                {
                    Title = "エラー",
                    Content = "先に元画像を読み込んでください。",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dlg.ShowAsync();
                return;
            }

            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            if (decoder.PixelWidth != _width || decoder.PixelHeight != _height)
            {
                var dlg = new ContentDialog
                {
                    Title = "エラー",
                    Content = "マスク画像のサイズが元画像と一致しません。",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dlg.ShowAsync();
                return;
            }

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            _mask8 = pixelData.DetachPixelData();
            _dst8 = new byte[_src8.Length];

            // サムネイル表示＋ラベル非表示
            MaskThumbnail.Source = CreateBitmapFromBytes(_mask8);
            MaskHintLabel.Visibility = Visibility.Collapsed;
           // MaskLabel.Visibility = Visibility.Collapsed;

            // 自動合成
            ProcessImageWithMask(_src8, _mask8, _dst8, _width, _height);
            ShowImage(_dst8);
        }

        // 合成
        void ProcessImageWithMask(byte[] src8, byte[] mask8, byte[] dst8, int width, int height)
        {
            int count = width * height;
            for (int i = 0; i < count; i++)
            {
                int idx = i * 4;
                dst8[idx + 0] = src8[idx + 0];
                dst8[idx + 1] = src8[idx + 1];
                dst8[idx + 2] = src8[idx + 2];
                dst8[idx + 3] = mask8[idx + 3];
            }
        }
        // RGBA (Straight) → BGRA (Premultiplied) に変換
        private byte[] ToBgraPremultiplied(byte[] rgba)
        {
            byte[] bgra = new byte[rgba.Length];

            for (int i = 0; i < rgba.Length; i += 4)
            {
                byte r = rgba[i + 0];
                byte g = rgba[i + 1];
                byte b = rgba[i + 2];
                byte a = rgba[i + 3];

                float af = a / 255f;

                // BGRA + Premultiplied
                bgra[i + 0] = (byte)(b * af); // B
                bgra[i + 1] = (byte)(g * af); // G
                bgra[i + 2] = (byte)(r * af); // R
                bgra[i + 3] = a;              // A
            }

            return bgra;
        }

        // 表示
        void ShowImage(byte[] pixelsRgba)
        {
            ImagePreview.Source = CreateBitmapFromBytes(pixelsRgba);
        }


        // 保存
        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_dst8 == null) return;

            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("PNG Image", new[] { ".png" });
            picker.SuggestedFileName = "output";
            InitializeWithWindow.Initialize(picker, GetWindowHandle());

            StorageFile file = await picker.PickSaveFileAsync();
            if (file == null) return;

            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

            encoder.SetPixelData(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Straight,
                (uint)_width,
                (uint)_height,
                96, 96,
                _dst8);

            await encoder.FlushAsync();
        }

        // D&D が動くように DragOver を明示
        private void OnCardDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
        }

        // D&D 基画像
        private async void OnBaseImageDrop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file)
                    await OpenBaseImageAsync(file);
            }
        }

        // D&D マスク画像
        private async void OnMaskImageDrop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file)
                    await OpenMaskImageAsync(file);
            }
        }

        // ホバー：枠内部を少し明るく
        private void OnCardPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 70, 70, 70));
            }
        }

        private void OnCardPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 42, 42));
            }
        }

        // クリック読み込み
        private async void OnBaseImageAreaTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            await OpenBaseImageAsync();
        }

        private async void OnMaskImageAreaTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            await OpenMaskImageAsync();
        }

        // ヘルプ
        private async void OnHelpClick(object sender, RoutedEventArgs e)
        {
            var uri = new Uri("https://example.com/help");
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }
}
