using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GravityCapture.Models;
using GravityCapture.Services;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private bool _haveCrop = false;
        private double _nx, _ny, _nw, _nh;
        private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
        private AppSettings _settings = AppSettings.Load();

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

        public MainWindow()
        {
            InitializeComponent();
            BindFromSettings();

            var ark = ScreenCapture.ResolveArkWindow();
            if (ark != IntPtr.Zero) _hwnd = ark;

            QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            QualitySlider.ValueChanged += (_, __) => QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            _previewTimer.Tick += (_, __) => UpdatePreview();

            Loaded += (_, __) =>
            {
                _previewTimer.Start();
                UpdatePreview();
            };
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            BindToSettings();
            _settings.Save();
            StatusText.Text = "Saved.";
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Start();
            StatusText.Text = "Preview running.";
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Stop();
            LivePreview.Source = null;
            StatusText.Text = "Preview stopped.";
        }

        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_hwnd == IntPtr.Zero)
            {
                var ark = ScreenCapture.ResolveArkWindow();
                if (ark != IntPtr.Zero) _hwnd = ark;
            }

            var (ok, rect, hwndUsed) = ScreenCapture.SelectRegion(_hwnd);
            if (!ok) { StatusText.Text = "Selection cancelled."; return; }
            if (hwndUsed != IntPtr.Zero) _hwnd = hwndUsed;

            bool normOk = (_hwnd != IntPtr.Zero)
                ? ScreenCapture.TryNormalizeRect(_hwnd, rect, out _nx, out _ny, out _nw, out _nh)
                : ScreenCapture.TryNormalizeRectDesktop(rect, out _nx, out _ny, out _nw, out _nh);

            _haveCrop = normOk;

            if (normOk)
            {
                _settings.UseCrop = true;
                _settings.CropX = _nx; _settings.CropY = _ny; _settings.CropW = _nw; _settings.CropH = _nh;
                _settings.Save();
                StatusText.Text = $"Crop set. nx={_nx:F3} ny={_ny:F3} nw={_nw:F3} nh={_nh:F3}";
            }
            else
            {
                StatusText.Text = "Failed to normalize crop.";
            }

            UpdatePreview();
        }

        // ===== OCR: crop -> paste text in box (and clipboard) =====
        private async void OcrCropBtn_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!_haveCrop) { StatusText.Text = "No crop set."; return; }
                if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) { StatusText.Text = "No target window."; return; }

                using var bmp = ScreenCapture.CaptureCropNormalized(_hwnd, _nx, _ny, _nw, _nh);
                LivePreview.Source = ToBitmapImage(bmp);

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                var ocr = new RemoteOcrService(_settings);
                var res = await ocr.ExtractAsync(ms, CancellationToken.None);

                var text = string.Join(Environment.NewLine,
                    (res?.Lines ?? Array.Empty<ExtractLine>())
                        .Select(l => l?.Text ?? string.Empty)
                        .Where(t => !string.IsNullOrWhiteSpace(t)));

                LogLineBox.Text = text;
                try { System.Windows.Clipboard.SetText(text); } catch { /* clipboard busy */ }

                StatusText.Text = "OCR done → text pasted.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"OCR failed: {ex.Message}";
            }
        }

        // ===== OCR + post to API (and paste text) =====
        private async void OcrAndPostNowBtn_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!_haveCrop) { StatusText.Text = "No crop set."; return; }
                if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) { StatusText.Text = "No target window."; return; }

                using var bmp = ScreenCapture.CaptureCropNormalized(_hwnd, _nx, _ny, _nw, _nh);
                LivePreview.Source = ToBitmapImage(bmp);

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                var apiKey  = _settings.Auth?.ApiKey ?? string.Empty;
                var channel = _settings.Image?.ChannelId ?? string.Empty;
                var tribe   = _settings.TribeName ?? string.Empty;

                var ingestor = new OcrIngestor();
                var result   = await ingestor.ScanAndPostAsync(ms, apiKey, channel, tribe, CancellationToken.None);

                var text = string.Join(Environment.NewLine,
                    (result?.Lines ?? Array.Empty<ExtractLine>())
                        .Select(l => l?.Text ?? string.Empty)
                        .Where(t => !string.IsNullOrWhiteSpace(t)));

                LogLineBox.Text = text;
                try { System.Windows.Clipboard.SetText(text); } catch { }

                StatusText.Text = "OCR posted.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Post failed: {ex.Message}";
            }
        }

        private void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Sent.";
        }

        private void UpdatePreview()
        {
            try
            {
                if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
                {
                    LivePreview.Source = null;
                    StatusText.Text = "No Ark window selected.";
                    return;
                }
                if (IsIconic(_hwnd))
                {
                    LivePreview.Source = null;
                    StatusText.Text = "Ark window minimized.";
                    return;
                }

                using var frameBmp = ScreenCapture.CaptureForPreview(_hwnd, out bool fallback, out string? why);

                Bitmap toShow = frameBmp;
                Bitmap? cropped = null;
                if (_haveCrop)
                {
                    int rx = Math.Clamp((int)Math.Round(_nx * frameBmp.Width), 0, frameBmp.Width - 1);
                    int ry = Math.Clamp((int)Math.Round(_ny * frameBmp.Height), 0, frameBmp.Height - 1);
                    int rw = Math.Clamp((int)Math.Round(_nw * frameBmp.Width), 1, frameBmp.Width - rx);
                    int rh = Math.Clamp((int)Math.Round(_nh * frameBmp.Height), 1, frameBmp.Height - ry);

                    cropped = new Bitmap(rw, rh, ImageFormatToPixelFormat());
                    using var g = Graphics.FromImage(cropped);
                    g.DrawImage(frameBmp, new Rectangle(0, 0, rw, rh), new Rectangle(rx, ry, rw, rh), GraphicsUnit.Pixel);
                    toShow = cropped;
                }

                LivePreview.Source = ToBitmapImage(toShow);
                cropped?.Dispose();

                StatusText.Text = fallback ? $"Preview: screen fallback ({why})" : "Preview: WGC";
            }
            catch (Exception ex)
            {
                LivePreview.Source = null;
                StatusText.Text = $"Preview error: {ex.Message}";
            }
        }

        private static PixelFormat ImageFormatToPixelFormat() => PixelFormat.Format32bppPArgb;

        private static BitmapImage ToBitmapImage(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }

        private void BindFromSettings()
        {
            ChannelBox.Text = _settings.Image?.ChannelId ?? "";
            ApiUrlBox.Text   = _settings.ApiBaseUrl ?? "";
            ApiKeyBox.Text   = _settings.Auth?.ApiKey ?? "";
            IntervalBox.Text = Math.Max(1, _settings.IntervalMinutes).ToString();

            ActiveWindowCheck.IsChecked = _settings.Capture?.ActiveWindow ?? false;

            ServerBox.Text = _settings.Capture?.ServerName ?? "";
            TribeBox.Text  = _settings.TribeName ?? "";

            QualitySlider.Value     = _settings.Image?.JpegQuality ?? 90;
            AutoOcrCheck.IsChecked  = _settings.AutoOcrEnabled;
            RedOnlyCheck.IsChecked  = _settings.PostOnlyCritical;

            FilterTameCheck.IsChecked    = _settings.Image?.FilterTameDeath ?? false;
            FilterStructCheck.IsChecked  = _settings.Image?.FilterStructureDestroyed ?? false;
            FilterTribeCheck.IsChecked   = _settings.Image?.FilterTribeMateDeath ?? false;

            if (_settings.UseCrop)
            {
                _nx = _settings.CropX; _ny = _settings.CropY; _nw = _settings.CropW; _nh = _settings.CropH;
                _haveCrop = _nw > 0 && _nh > 0;
            }
        }

        private void BindToSettings()
        {
            _settings.Image   ??= new AppSettings.ImageSettings();
            _settings.Capture ??= new AppSettings.CaptureSettings();
            _settings.Auth    ??= new AppSettings.AuthSettings();

            _settings.Image.ChannelId = ChannelBox.Text?.Trim() ?? "";
            _settings.ApiBaseUrl      = ApiUrlBox.Text?.Trim() ?? "";
            _settings.Auth.ApiKey     = ApiKeyBox.Text?.Trim() ?? "";
            _settings.TribeName       = TribeBox.Text?.Trim() ?? "";

            _settings.IntervalMinutes = int.TryParse(IntervalBox.Text, out var mins) && mins > 0 ? mins : 1;
            _settings.Capture.ActiveWindow = ActiveWindowCheck.IsChecked == true;
            _settings.Capture.ServerName   = ServerBox.Text?.Trim() ?? "";

            _settings.Image.JpegQuality = (int)QualitySlider.Value;
            _settings.AutoOcrEnabled    = AutoOcrCheck.IsChecked == true;
            _settings.PostOnlyCritical  = RedOnlyCheck.IsChecked == true;

            _settings.Image.FilterTameDeath          = FilterTameCheck.IsChecked == true;
            _settings.Image.FilterStructureDestroyed = FilterStructCheck.IsChecked == true;
            _settings.Image.FilterTribeMateDeath     = FilterTribeCheck.IsChecked == true;

            if (_haveCrop)
            {
                _settings.UseCrop = true;
                _settings.CropX = _nx; _settings.CropY = _ny; _settings.CropW = _nw; _settings.CropH = _nh;
            }
        }
    }
}
