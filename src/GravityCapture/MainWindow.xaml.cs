#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;            // <-- needed for Canvas
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

        private byte[]? _lastCropPng;
        private byte[]? _lastBinarizedPng;
        private string? _lastOcrJson;
        private OcrBox[] _lastBoxes = Array.Empty<OcrBox>();
        private int _lastImgPixelW, _lastImgPixelH;

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_19 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_20 = 20;
        private void EnableDarkTitleBar()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int on = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_20, ref on, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_19, ref on, sizeof(int));
        }

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
                EnableDarkTitleBar();
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
            OcrOverlay.Children.Clear();
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

        private async void OcrCropBtn_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!_haveCrop) { StatusText.Text = "No crop set."; return; }
                if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) { StatusText.Text = "No target window."; return; }

                using var bmp = ScreenCapture.CaptureCropNormalized(_hwnd, _nx, _ny, _nw, _nh);
                var img = ToBitmapImage(bmp, out int pxW, out int pxH);
                _lastImgPixelW = pxW; _lastImgPixelH = pxH;
                LivePreview.Source = img;

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                _lastCropPng = ms.ToArray();

                var ocr = new RemoteOcrService(_settings);
                var dbg = await ocr.ExtractWithDebugAsync(new MemoryStream(_lastCropPng), default);

                _lastOcrJson = dbg.RawJson;
                _lastBinarizedPng = dbg.BinarizedPng;
                _lastBoxes = dbg.Boxes.Select(b => new OcrBox(b.X, b.Y, b.W, b.H, b.Conf, b.Text)).ToArray();

                var text = string.Join(Environment.NewLine, dbg.LinesText ?? Array.Empty<string>());
                LogLineBox.Text = text;
                TryCopyText(text);

                RenderOcrOverlay();
                StatusText.Text = "OCR done → text pasted.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"OCR failed: {ex.Message}";
            }
        }

        private async void OcrAndPostNowBtn_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!_haveCrop) { StatusText.Text = "No crop set."; return; }
                if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) { StatusText.Text = "No target window."; return; }

                using var bmp = ScreenCapture.CaptureCropNormalized(_hwnd, _nx, _ny, _nw, _nh);
                var img = ToBitmapImage(bmp, out int pxW, out int pxH);
                _lastImgPixelW = pxW; _lastImgPixelH = pxH;
                LivePreview.Source = img;

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                _lastCropPng = ms.ToArray();

                var ocr = new RemoteOcrService(_settings);
                var dbg = await ocr.ExtractWithDebugAsync(new MemoryStream(_lastCropPng), default);

                _lastOcrJson = dbg.RawJson;
                _lastBinarizedPng = dbg.BinarizedPng;
                _lastBoxes = dbg.Boxes.Select(b => new OcrBox(b.X, b.Y, b.W, b.H, b.Conf, b.Text)).ToArray();
                var text = string.Join(Environment.NewLine, dbg.LinesText ?? Array.Empty<string>());
                LogLineBox.Text = text;
                TryCopyText(text);
                RenderOcrOverlay();

                var apiKey  = _settings.Auth?.ApiKey ?? string.Empty;
                var channel = _settings.Image?.ChannelId ?? string.Empty;
                var tribe   = _settings.TribeName ?? string.Empty;

                var ingestor = new OcrIngestor();
                _ = await ingestor.ScanAndPostAsync(new MemoryStream(_lastCropPng), apiKey, channel, tribe, default);

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

        private void SaveDebugBtn_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (_lastCropPng is null || _lastOcrJson is null)
                {
                    StatusText.Text = "No OCR run yet.";
                    return;
                }

                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save debug ZIP",
                    Filter = "ZIP files (*.zip)|*.zip",
                    FileName = $"gravity_debug_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
                };
                if (sfd.ShowDialog() != true) return;

                using var fs = File.Create(sfd.FileName);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

                var e1 = zip.CreateEntry("crop.png");
                using (var z = e1.Open()) z.Write(_lastCropPng, 0, _lastCropPng.Length);

                if (_lastBinarizedPng is not null)
                {
                    var e2 = zip.CreateEntry("binarized.png");
                    using var z2 = e2.Open();
                    z2.Write(_lastBinarizedPng, 0, _lastBinarizedPng.Length);
                }

                var e3 = zip.CreateEntry("response.json");
                using (var w = new StreamWriter(e3.Open())) w.Write(_lastOcrJson);

                var e4 = zip.CreateEntry("settings.json");
                using (var w2 = new StreamWriter(e4.Open()))
                    w2.Write(JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));

                StatusText.Text = "Debug ZIP saved.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Save ZIP failed: {ex.Message}";
            }
        }

        private void ShowOcrDetailsCheck_Changed(object sender, RoutedEventArgs e) => RenderOcrOverlay();
        private void LivePreview_SizeChanged(object sender, SizeChangedEventArgs e) => RenderOcrOverlay();

        private void RenderOcrOverlay()
        {
            OcrOverlay.Children.Clear();
            if (LivePreview.Source is not BitmapSource src) return;
            if (ShowOcrDetailsCheck?.IsChecked != true) return;
            if (_lastBoxes.Length == 0) return;

            double viewW = LivePreview.ActualWidth;
            double viewH = LivePreview.ActualHeight;
            if (viewW <= 0 || viewH <= 0) return;

            int pxW = _lastImgPixelW > 0 ? _lastImgPixelW : src.PixelWidth;
            int pxH = _lastImgPixelH > 0 ? _lastImgPixelH : src.PixelHeight;

            double scale = Math.Min(viewW / pxW, viewH / pxH);
            double xOff = (viewW - pxW * scale) / 2.0;
            double yOff = (viewH - pxH * scale) / 2.0;

            foreach (var b in _lastBoxes)
            {
                var r = new System.Windows.Shapes.Rectangle
                {
                    Stroke = System.Windows.Media.Brushes.Lime,
                    StrokeThickness = 1,
                    Fill = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(40, 0, 255, 0))
                };
                double x = xOff + b.X * scale;
                double y = yOff + b.Y * scale;
                double w = Math.Max(1, b.W * scale);
                double h = Math.Max(1, b.H * scale);

                Canvas.SetLeft(r, x);
                Canvas.SetTop(r, y);
                r.Width = w;
                r.Height = h;
                r.ToolTip = $"{b.Conf:P0}  {b.Text}";
                OcrOverlay.Children.Add(r);
            }
        }

        private void UpdatePreview()
        {
            try
            {
                if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
                {
                    LivePreview.Source = null;
                    OcrOverlay.Children.Clear();
                    StatusText.Text = "No Ark window selected.";
                    return;
                }
                if (IsIconic(_hwnd))
                {
                    LivePreview.Source = null;
                    OcrOverlay.Children.Clear();
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

                    cropped = new Bitmap(rw, rh, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using var g = Graphics.FromImage(cropped);
                    g.DrawImage(frameBmp, new Rectangle(0, 0, rw, rh),
                        new Rectangle(rx, ry, rw, rh), GraphicsUnit.Pixel);
                    toShow = cropped;
                }

                LivePreview.Source = ToBitmapImage(toShow, out _lastImgPixelW, out _lastImgPixelH);
                cropped?.Dispose();

                RenderOcrOverlay();

                StatusText.Text = fallback ? $"Preview: screen fallback ({why})" : "Preview: WGC";
            }
            catch (Exception ex)
            {
                LivePreview.Source = null;
                OcrOverlay.Children.Clear();
                StatusText.Text = $"Preview error: {ex.Message}";
            }
        }

        private static void TryCopyText(string text)
        {
            try { System.Windows.Clipboard.SetText(text); } catch { }
        }

        private static BitmapImage ToBitmapImage(Bitmap bmp, out int pixelW, out int pixelH)
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
            pixelW = img.PixelWidth;
            pixelH = img.PixelHeight;
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

        private readonly struct OcrBox
        {
            public readonly double X, Y, W, H, Conf;
            public readonly string Text;
            public OcrBox(double x, double y, double w, double h, double conf, string? text)
            { X = x; Y = y; W = w; H = h; Conf = conf; Text = text ?? ""; }
        }
    }
}
