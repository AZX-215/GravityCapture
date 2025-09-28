#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;            // Canvas, Image
using System.Windows.Media;
using System.Windows.Media.Imaging;

using GravityCapture.Models;              // AppSettings
using GravityCapture.Services;            // ScreenCapture

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        // Settings persisted by the partial in MainWindow.Persistence.cs
        private readonly AppSettings _settings = AppSettings.Load();

        // runtime state
        private IntPtr _arkHwnd = IntPtr.Zero;

        private bool _hasCrop;
        private double _nx, _ny, _nw, _nh;    // normalized crop (0..1, window space)
        private Rectangle _lastRawRect;

        private readonly System.Timers.Timer _previewTimer;
        private readonly object _frameLock = new();
        private Bitmap? _lastPreviewBmp;

        private bool _showOcrDetails;

        public MainWindow()
        {
            InitializeComponent();

            _previewTimer = new System.Timers.Timer(1000.0 / 6.0); // ~6 FPS
            _previewTimer.Elapsed += (_, __) => TryUpdatePreview();
            _previewTimer.AutoReset = true;

            Loaded  += Window_Loaded;
            Closed  += Window_Closed;
        }

        // ---------- lifecycle ----------
        private void Window_Loaded(object? sender, RoutedEventArgs e)
        {
            _arkHwnd = ScreenCapture.ResolveArkWindow();
            Status("Ready" + (_arkHwnd == IntPtr.Zero ? " — No Ark window selected." : ""));
            _previewTimer.Start();
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _previewTimer.Stop();
            lock (_frameLock)
            {
                _lastPreviewBmp?.Dispose();
                _lastPreviewBmp = null;
            }
        }

        // ---------- top buttons ----------
        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BindToSettings();
                _settings.Save();
                Status("Settings saved.");
            }
            catch (Exception ex)
            {
                Status("Save failed: " + ex.Message);
            }
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Start();
            Status("Preview running.");
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Stop();
            Status("Preview stopped.");
        }

        // ---------- crop selection ----------
        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            EnsureArkWindow();

            // Simple, robust selector (stubbed if the overlay is not present)
            var (ok, rect, hwndUsed) = ScreenCapture.SelectRegion(_arkHwnd);
            if (!ok || rect.Width <= 0 || rect.Height <= 0)
            {
                Status("Selection canceled.");
                return;
            }

            if (hwndUsed != IntPtr.Zero)
                _arkHwnd = hwndUsed;

            _lastRawRect = rect;

            if (ScreenCapture.TryNormalizeRect(_arkHwnd, rect, out _nx, out _ny, out _nw, out _nh))
            {
                _hasCrop = true;
                Status($"Crop set  nx={_nx:F3} ny={_ny:F3} nw={_nw:F3} nh={_nh:F3}");
                Dispatcher.Invoke(RedrawOverlay);
            }
            else
            {
                _hasCrop = false;
                Status("Failed to normalize selection.");
            }
        }

        // ---------- OCR helpers ----------
        private void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            EnsureArkWindow();
            if (!_hasCrop)
            {
                Status("No crop set.");
                return;
            }

            try
            {
                using Bitmap bmp = ScreenCapture.CaptureCropNormalized(_arkHwnd, _nx, _ny, _nw, _nh);
                // For quick testing copy to clipboard
                System.Windows.Clipboard.SetImage(ToBitmapSource(bmp)); // WPF clipboard
                Status("Cropped image copied to clipboard.");
            }
            catch (Exception ex)
            {
                Status("Crop failed: " + ex.Message);
            }
        }

        private void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            Status("Not implemented in this build. Use OCR Crop → Paste.");
        }

        private void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(LogLineBox.Text))
            {
                Status("Nothing to send.");
                return;
            }

            // Wire to your ingest client if needed.
            Status("Sent pasted line (local stub).");
        }

        // ---------- debug UI ----------
        private void ShowOcrDetailsCheck_Changed(object sender, RoutedEventArgs e)
        {
            _showOcrDetails = (ShowOcrDetailsCheck.IsChecked == true);
            RedrawOverlay();
        }

        private void SaveDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string root = System.IO.Path.Combine(desktop, "GravityCapture_Debug_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string zipPath = root + ".zip";

            Directory.CreateDirectory(root);

            try
            {
                lock (_frameLock)
                {
                    _lastPreviewBmp?.Save(System.IO.Path.Combine(root, "preview.png"));
                }

                if (_hasCrop)
                {
                    File.WriteAllText(System.IO.Path.Combine(root, "crop.json"),
                        $"{{\"nx\":{_nx},\"ny\":{_ny},\"nw\":{_nw},\"nh\":{_nh}}}");
                }

                var sb = new StringBuilder();
                sb.AppendLine("arkHwnd: " + _arkHwnd);
                sb.AppendLine("hasCrop: " + _hasCrop);
                sb.AppendLine("timestamp: " + DateTime.Now.ToString("O"));
                File.WriteAllText(System.IO.Path.Combine(root, "meta.txt"), sb.ToString());

                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(root, zipPath);
                Directory.Delete(root, true);

                Status("Saved: " + zipPath);
            }
            catch (Exception ex)
            {
                Status("Save debug ZIP failed: " + ex.Message);
            }
        }

        // ---------- preview loop ----------
        private void TryUpdatePreview()
        {
            try
            {
                EnsureArkWindow();
                if (_arkHwnd == IntPtr.Zero)
                {
                    Dispatcher.Invoke(() =>
                    {
                        LivePreview.Source = null;
                        Status("No Ark window selected.");
                        RedrawOverlay(); // clears
                    });
                    return;
                }

                bool usedFallback;
                string? reason;
                using Bitmap bmp = ScreenCapture.CaptureForPreview(_arkHwnd, out usedFallback, out reason);

                lock (_frameLock)
                {
                    _lastPreviewBmp?.Dispose();
                    _lastPreviewBmp = (Bitmap)bmp.Clone();
                }

                Dispatcher.Invoke(() =>
                {
                    LivePreview.Source = ToBitmapSource(bmp);
                    if (usedFallback && !string.IsNullOrEmpty(reason))
                        Status("Preview: screen fallback (" + reason + ")");
                    else if (usedFallback)
                        Status("Preview: screen fallback");
                    else
                        Status("Preview: WGC");

                    RedrawOverlay();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    Status("Preview error: " + ex.Message);
                    LivePreview.Source = null;
                    RedrawOverlay();
                });
            }
        }

        // ---------- overlay ----------
        private void LivePreview_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawOverlay();

        private void RedrawOverlay()
        {
            OcrOverlay.Children.Clear();
            if (!_showOcrDetails || !_hasCrop || LivePreview.Source == null)
                return;

            var src = (BitmapSource)LivePreview.Source;
            double iw = src.PixelWidth;
            double ih = src.PixelHeight;

            double cw = LivePreview.ActualWidth;
            double ch = LivePreview.ActualHeight;

            if (iw <= 0 || ih <= 0 || cw <= 0 || ch <= 0)
                return;

            double scale = Math.Min(cw / iw, ch / ih);
            double vw = iw * scale;
            double vh = ih * scale;

            double ox = (cw - vw) / 2.0;
            double oy = (ch - vh) / 2.0;

            double rx = ox + _nx * vw;
            double ry = oy + _ny * vh;
            double rw = _nw * vw;
            double rh = _nh * vh;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, rw),
                Height = Math.Max(1, rh),
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(125, 127, 255)),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 125, 127, 255))
            };

            Canvas.SetLeft(rect, rx);
            Canvas.SetTop(rect, ry);
            OcrOverlay.Children.Add(rect);
        }

        // ---------- helpers ----------
        private void EnsureArkWindow()
        {
            if (_arkHwnd == IntPtr.Zero)
                _arkHwnd = ScreenCapture.ResolveArkWindow();
        }

        private static BitmapSource ToBitmapSource(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        // Persist current inputs → settings (minimal; extend as needed)
        private void BindToSettings()
        {
            _settings.ChannelId   = ChannelBox.Text?.Trim() ?? "";
            _settings.ApiUrl      = ApiUrlBox.Text?.Trim() ?? "";
            _settings.ApiKey      = ApiKeyBox.Text?.Trim() ?? "";
            _settings.Server      = ServerBox.Text?.Trim() ?? "";
            _settings.Tribe       = TribeBox.Text?.Trim() ?? "";
            _settings.JpegQuality = (int)QualitySlider.Value;
            _settings.ActiveWindowOnly = ActiveWindowCheck.IsChecked == true;
        }

        private void Status(string text) => StatusText.Text = text;
    }
}
