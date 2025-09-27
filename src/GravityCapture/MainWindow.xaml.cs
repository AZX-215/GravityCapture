#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;                  // Canvas
using System.Windows.Media;                     // WPF colors/brushes
using System.Windows.Media.Imaging;
using GravityCapture.Models;
using GravityCapture.Services;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private readonly System.Windows.Threading.DispatcherTimer _previewTimer;
        private AppSettings _settings = AppSettings.Load();
        private IntPtr _arkHwnd = IntPtr.Zero;

        // last OCR result for overlay
        private ExtractResponse? _lastOcr;
        private BitmapSource? _lastPreview;
        private readonly object _gate = new();

        public MainWindow()
        {
            InitializeComponent();

            LoadFromSettings();
            _previewTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            _previewTimer.Tick += async (_, __) => await RefreshPreviewAsync();

            _previewTimer.Start();
            SetStatus("Idle.");
        }

        // ---------------- bind helpers ----------------

        private void LoadFromSettings()
        {
            ChannelBox.Text          = _settings.ChannelId ?? "";
            ApiUrlBox.Text           = _settings.ApiBaseUrl ?? "";
            ApiKeyBox.Text           = _settings.ApiKey ?? "";
            ServerBox.Text           = _settings.Server ?? "";
            TribeBox.Text            = _settings.Tribe ?? "";
            IntervalBox.Text         = (_settings.IntervalMinutes > 0 ? _settings.IntervalMinutes : 1).ToString();
            QualitySlider.Value      = (_settings.JpegQuality > 0 ? _settings.JpegQuality : 85);
            ActiveWindowCheck.IsChecked = _settings.ActiveWindowOnly;
        }

        private void BindToSettings()   // called by MainWindow.Persistence.cs on app close
        {
            _settings.ChannelId        = ChannelBox.Text.Trim();
            _settings.ApiBaseUrl       = ApiUrlBox.Text.Trim();
            _settings.ApiKey           = ApiKeyBox.Text.Trim();
            _settings.Server           = ServerBox.Text.Trim();
            _settings.Tribe            = TribeBox.Text.Trim();
            _settings.ActiveWindowOnly = ActiveWindowCheck.IsChecked == true;

            if (int.TryParse(IntervalBox.Text, out var mins) && mins > 0)
                _settings.IntervalMinutes = mins;

            _settings.JpegQuality = (int)Math.Round(QualitySlider.Value);
        }

        // ---------------- UI helpers ----------------

        private void SetStatus(string msg) => StatusText.Text = msg;

        private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }

        private void DrawOcrOverlay()
        {
            OcrOverlay.Children.Clear();
            if (ShowOcrDetailsCheck.IsChecked != true || _lastOcr == null || _lastPreview == null)
                return;

            double w = _lastPreview.PixelWidth;
            double h = _lastPreview.PixelHeight;
            if (w <= 0 || h <= 0) return;

            double vw = LivePreview.ActualWidth;
            double vh = LivePreview.ActualHeight;
            if (vw <= 0 || vh <= 0) return;

            double sx = vw / w;
            double sy = vh / h;

            foreach (var line in _lastOcr.Lines)
            {
                if (line.Bbox == null || line.Bbox.Length != 4) continue;

                double x  = line.Bbox[0] * sx;
                double y  = line.Bbox[1] * sy;
                double rw = line.Bbox[2] * sx;
                double rh = line.Bbox[3] * sy;

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Stroke = System.Windows.Media.Brushes.Lime,
                    StrokeThickness = 1.2,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(32, 0, 255, 0)),
                    Width = Math.Max(1, rw),
                    Height = Math.Max(1, rh),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                OcrOverlay.Children.Add(rect);
            }
        }

        // ---------------- Buttons ----------------

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            BindToSettings();
            _settings.Save();
            SetStatus("Saved.");
        }

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            await RefreshPreviewAsync();
            _previewTimer.Start();
            SetStatus("Preview started.");
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Stop();
            SetStatus("Preview stopped.");
        }

        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            _arkHwnd = WindowUtil.FindBestWindowByTitleHint("Ark");
            if (_arkHwnd == IntPtr.Zero)
            {
                SetStatus("Ark window not found. Put Ark in windowed/borderless and try again.");
                return;
            }
            SetStatus($"Selected window: {WindowUtil.GetWindowDebugName(_arkHwnd)}");
        }

        private async void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = EnsureArkHwnd();
                if (hwnd == IntPtr.Zero) { SetStatus("No Ark window selected."); return; }

                using var bmp = _settings.UseCrop
                    ? ScreenCapture.CaptureCropNormalized(hwnd, _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH)
                    : ScreenCapture.Capture(hwnd);

                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var remote = new RemoteOcrService(_settings);
                var res = await remote.ExtractAsync(ms, CancellationToken.None).ConfigureAwait(true);
                _lastOcr = res;

                var sb = new StringBuilder();
                foreach (var ln in res.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)))
                    sb.AppendLine(ln.Text!.Trim());
                LogLineBox.Text = sb.ToString();

                SetStatus($"OCR lines: {res.Lines.Count}  conf: {res.Conf:0.###}");
                DrawOcrOverlay();
            }
            catch (Exception ex)
            {
                SetStatus($"OCR error: {ex.Message}");
            }
        }

        private async void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = EnsureArkHwnd();
                if (hwnd == IntPtr.Zero) { SetStatus("No Ark window selected."); return; }

                var ingestor = new OcrIngestor();
                await ingestor.ScanAndPostAsync(
                    hwnd,
                    _settings,
                    _settings.Server ?? "",
                    _settings.Tribe ?? "",
                    s => Dispatcher.Invoke(() => SetStatus(s))
                ).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                SetStatus($"OCR/Post error: {ex.Message}");
            }
        }

        private async void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            var text = (LogLineBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text)) { SetStatus("Nothing to send."); return; }

            try
            {
                // LogIngestClient is static in your repo; call the static method.
                await LogIngestClient.SendParsedAsync(
                    text,
                    _settings.Server ?? "",
                    _settings.Tribe ?? "",
                    _settings,                       // carries API base/key
                    CancellationToken.None
                ).ConfigureAwait(true);

                SetStatus("Sent.");
            }
            catch (Exception ex)
            {
                SetStatus($"Send failed: {ex.Message}");
            }
        }

        private void ShowOcrDetailsCheck_Changed(object sender, RoutedEventArgs e) => DrawOcrOverlay();
        private void LivePreview_SizeChanged(object sender, SizeChangedEventArgs e)    => DrawOcrOverlay();

        private async void SaveDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var stamp  = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var baseDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    $"gc_debug_{stamp}");
                Directory.CreateDirectory(baseDir);

                lock (_gate)
                {
                    if (_lastPreview != null)
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(_lastPreview));
                        using var fs = File.Create(System.IO.Path.Combine(baseDir, "preview.png"));
                        enc.Save(fs);
                    }
                }

                if (_lastOcr != null)
                {
                    await File.WriteAllTextAsync(
                        System.IO.Path.Combine(baseDir, "ocr.json"),
                        System.Text.Json.JsonSerializer.Serialize(_lastOcr, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }

                await File.WriteAllTextAsync(
                    System.IO.Path.Combine(baseDir, "appsettings.json"),
                    System.Text.Json.JsonSerializer.Serialize(_settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                SetStatus($"Saved debug to {baseDir}");
            }
            catch (Exception ex)
            {
                SetStatus($"Save debug failed: {ex.Message}");
            }
        }

        // ---------------- Preview loop ----------------

        private async Task RefreshPreviewAsync()
        {
            try
            {
                var hwnd = EnsureArkHwnd();
                if (hwnd == IntPtr.Zero)
                {
                    LivePreview.Source = null;
                    _lastPreview = null;
                    SetStatus("No Ark window selected.");
                    return;
                }

                using var bmp = ScreenCapture.Capture(hwnd);
                var src = ToBitmapSource(bmp);

                lock (_gate) _lastPreview = src;
                LivePreview.Source = src;

                if (ShowOcrDetailsCheck.IsChecked == true)
                    DrawOcrOverlay();
            }
            catch (Exception ex)
            {
                SetStatus($"Preview error: {ex.Message}");
                await Task.Delay(1000);
            }
        }

        private IntPtr EnsureArkHwnd()
        {
            if (_settings.ActiveWindowOnly == true)
                _arkHwnd = WindowUtil.GetForegroundWindow();

            if (_arkHwnd == IntPtr.Zero)
                _arkHwnd = WindowUtil.FindBestWindowByTitleHint("Ark");

            return _arkHwnd;
        }
    }
}
