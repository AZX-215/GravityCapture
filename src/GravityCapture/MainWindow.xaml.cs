#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;             // <-- fixes CS0103 'Canvas'
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using GravityCapture.Models;
using GravityCapture.Services;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _previewTimer;
        private AppSettings _settings = AppSettings.Load();
        private IntPtr _arkHwnd = IntPtr.Zero;

        // last OCR result for overlay
        private ExtractResponse? _lastOcr;
        private BitmapSource? _lastPreview;
        private readonly object _gate = new();

        public MainWindow()
        {
            InitializeComponent();

            // hydrate UI from settings
            ChannelBox.Text   = _settings.ChannelId ?? "";
            ApiUrlBox.Text    = _settings.ApiBaseUrl ?? "";
            ApiKeyBox.Text    = _settings.ApiKey ?? "";
            ServerBox.Text    = _settings.Server ?? "";
            TribeBox.Text     = _settings.Tribe ?? "";
            IntervalBox.Text  = (_settings.IntervalMinutes > 0 ? _settings.IntervalMinutes : 1).ToString();
            QualitySlider.Value = (_settings.JpegQuality > 0 ? _settings.JpegQuality : 85);
            ActiveWindowCheck.IsChecked = _settings.ActiveWindowOnly;

            // preview timer
            _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            _previewTimer.Tick += async (_, __) => await RefreshPreviewAsync();

            // start light preview loop (user can stop with Stop)
            _previewTimer.Start();
            SetStatus("Idle.");
        }

        // ---------------- UI helpers ----------------

        private void SetStatus(string msg)
        {
            StatusText.Text = msg;
        }

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

            // scale overlay Canvas to current image render size
            double w = _lastPreview.PixelWidth;
            double h = _lastPreview.PixelHeight;
            if (w <= 0 || h <= 0) return;

            // Canvas is stretched with the Image; use ActualWidth/Height to scale boxes
            double vw = LivePreview.ActualWidth;
            double vh = LivePreview.ActualHeight;
            if (vw <= 0 || vh <= 0) return;

            double sx = vw / w;
            double sy = vh / h;

            foreach (var line in _lastOcr.Lines)
            {
                if (line.Bbox == null || line.Bbox.Length != 4) continue;
                var x = line.Bbox[0] * sx;
                var y = line.Bbox[1] * sy;
                var rw = line.Bbox[2] * sx;
                var rh = line.Bbox[3] * sy;

                var rect = new Rectangle
                {
                    Stroke = Brushes.Lime,
                    StrokeThickness = 1.2,
                    Fill = new SolidColorBrush(Color.FromArgb(32, 0, 255, 0)),
                    Width = Math.Max(1, rw),
                    Height = Math.Max(1, rh),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                OcrOverlay.Children.Add(rect);
            }
        }

        // ---------------- Button handlers ----------------

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            // read back into settings and persist
            _settings.ChannelId = ChannelBox.Text.Trim();
            _settings.ApiBaseUrl = ApiUrlBox.Text.Trim();
            _settings.ApiKey = ApiKeyBox.Text.Trim();
            _settings.Server = ServerBox.Text.Trim();
            _settings.Tribe = TribeBox.Text.Trim();

            if (int.TryParse(IntervalBox.Text, out var mins) && mins > 0) _settings.IntervalMinutes = mins;
            _settings.JpegQuality = (int)Math.Round(QualitySlider.Value);
            _settings.ActiveWindowOnly = ActiveWindowCheck.IsChecked == true;

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

        // simple select: resolve Ark window by best title match if not already selected
        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            // try to lock Ark window (title hint “Ark” works for ASA; hit-test path is handled by capture)
            _arkHwnd = WindowUtil.FindBestWindowByTitleHint("Ark");
            if (_arkHwnd == IntPtr.Zero)
            {
                SetStatus("Ark window not found. Put Ark in windowed/borderless and try again.");
                return;
            }

            // keep existing normalized crop in settings; user overlay remains in your RegionSelector window
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

                // paste joined text for quick testing
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

                // As the ingest currently keeps posting out (parser+ingest wiring is external),
                // keep UI responsive.
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
                // lightweight client for pasted lines — use your existing API surface
                var client = new LogIngestClient(_settings);
                await client.SendParsedAsync(text, _settings.Server ?? "", _settings.Tribe ?? "", CancellationToken.None)
                            .ConfigureAwait(true);
                SetStatus("Sent.");
            }
            catch (Exception ex)
            {
                SetStatus($"Send failed: {ex.Message}");
            }
        }

        private void ShowOcrDetailsCheck_Changed(object sender, RoutedEventArgs e)
            => DrawOcrOverlay();

        private void LivePreview_SizeChanged(object sender, SizeChangedEventArgs e)
            => DrawOcrOverlay();

        private async void SaveDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // dump crop.png + ocr.json + appsettings.json to a timestamped folder on Desktop
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    $"gc_debug_{stamp}");
                Directory.CreateDirectory(baseDir);

                // latest preview image
                lock (_gate)
                {
                    if (_lastPreview != null)
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(_lastPreview));
                        using var fs = File.Create(Path.Combine(baseDir, "preview.png"));
                        enc.Save(fs);
                    }
                }

                if (_lastOcr != null)
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(baseDir, "ocr.json"),
                        System.Text.Json.JsonSerializer.Serialize(_lastOcr, new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true
                        }));
                }

                // settings snapshot
                await File.WriteAllTextAsync(
                    Path.Combine(baseDir, "appsettings.json"),
                    System.Text.Json.JsonSerializer.Serialize(_settings, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));

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

                using var bmp = ScreenCapture.Capture(hwnd); // ScreenCapture handles fallback internally
                var src = ToBitmapSource(bmp);

                lock (_gate) _lastPreview = src;
                LivePreview.Source = src;

                if (ShowOcrDetailsCheck.IsChecked == true)
                    DrawOcrOverlay();
            }
            catch (Exception ex)
            {
                // non-fatal; keep loop running
                SetStatus($"Preview error: {ex.Message}");
                await Task.Delay(1000);
            }
        }

        private IntPtr EnsureArkHwnd()
        {
            if (_settings.ActiveWindowOnly == true)
            {
                _arkHwnd = WindowUtil.GetForegroundWindow();
            }

            if (_arkHwnd == IntPtr.Zero)
                _arkHwnd = WindowUtil.FindBestWindowByTitleHint("Ark");

            return _arkHwnd;
        }
    }
}
