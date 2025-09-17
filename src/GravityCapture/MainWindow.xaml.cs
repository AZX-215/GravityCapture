using System;
using System.Drawing;
using System.IO;
using System.Timers;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Drawing.Imaging;

using GravityCapture.Models;
using GravityCapture.Services;
using GravityCapture.Views; // RegionSelectorWindow

// Aliases to disambiguate WPF vs WinForms types
using WpfMessageBox = System.Windows.MessageBox;
using WpfCursors    = System.Windows.Input.Cursors;
using WpfMouse      = System.Windows.Input.Mouse;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_NEW = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private readonly AppSettings _settings;
        private readonly System.Timers.Timer _timer;
        private ApiClient? _api;

        // Remember the window you selected a crop from (valid for this run only)
        private IntPtr _lastCropHwnd = IntPtr.Zero;

        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, __) => ApplyDarkTitleBar();

            _settings = AppSettings.Load();

            ApiUrlBox.Text = _settings.ApiUrl;
            ApiKeyBox.Text = _settings.ApiKey;
            ChannelBox.Text = _settings.ChannelId == 0 ? "" : _settings.ChannelId.ToString();
            IntervalBox.Text = _settings.IntervalMinutes.ToString();
            ActiveWindowCheck.IsChecked = _settings.CaptureActiveWindow;
            QualitySlider.Value = _settings.JpegQuality;
            QualityLabel.Text = _settings.JpegQuality.ToString();
            QualitySlider.ValueChanged += (_, __) => QualityLabel.Text = ((int)QualitySlider.Value).ToString();

            TitleHintBox.Text = _settings.TargetWindowHint;

            _timer = new System.Timers.Timer { AutoReset = true, Enabled = false };
            _timer.Elapsed += OnTick;

            if (_settings.Autostart) StartCapture();
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int enable = 1;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_NEW, ref enable, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enable, sizeof(int));
            }
            catch { }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            Status("Saved.");
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            StartCapture();
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e) => StopCapture();

        private void SaveSettings()
        {
            _settings.ApiUrl = ApiUrlBox.Text.TrimEnd('/');
            _settings.ApiKey = ApiKeyBox.Text.Trim();
            _settings.CaptureActiveWindow = ActiveWindowCheck.IsChecked == true;
            _settings.IntervalMinutes = int.TryParse(IntervalBox.Text, out var m) ? Math.Max(1, m) : 5;
            _settings.JpegQuality = (int)QualitySlider.Value;
            _settings.ChannelId = ulong.TryParse(ChannelBox.Text, out var ch) ? ch : 0;

            // window title hint (e.g., "ARK: Survival Ascended")
            _settings.TargetWindowHint = TitleHintBox.Text ?? "";

            _settings.Save();
        }

        private void StartCapture()
        {
            _api = new ApiClient(_settings.ApiUrl, _settings.ApiKey);
            _timer.Interval = TimeSpan.FromMinutes(_settings.IntervalMinutes).TotalMilliseconds;
            _timer.Start();
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            Status($"Running – every {_settings.IntervalMinutes} min.");
            _ = CaptureOnceAsync();
        }

        private void StopCapture()
        {
            _timer.Stop();
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            Status("Stopped.");
        }

        private async void OnTick(object? s, ElapsedEventArgs e) => await CaptureOnceAsync();

        private async System.Threading.Tasks.Task CaptureOnceAsync()
        {
            if (_api == null) return;
            try
            {
                Bitmap bmp;
                var hwnd = ResolveTargetWindow();
                if (_settings.UseCrop)
                    bmp = ScreenCapture.CaptureCropNormalized(hwnd, _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);
                else
                    bmp = ScreenCapture.Capture(_settings.CaptureActiveWindow);

                using (bmp)
                {
                    var bytes = ScreenCapture.ToJpegBytes(bmp, _settings.JpegQuality);
                    string fname = $"gravity_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                    bool ok = await _api.SendScreenshotAsync(bytes, fname, _settings.ChannelId, "Gravity capture");
                    Status(ok ? $"Sent {fname}" : "Send failed (HTTP)");
                }
            }
            catch (Exception ex)
            {
                Status("Error: " + ex.Message);
            }
        }

        private void Status(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

        // -------- Buttons --------

        private async void SendTestBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            WpfMouse.OverrideCursor = WpfCursors.Wait;
            Status("Posting stage test…");
            try
            {
                var (ok, error) = await LogIngestClient.SendTestAsync();
                if (ok)
                {
                    Status("Stage test event posted ✅");
                    WpfMessageBox.Show("Posted to staging API ✅", "Gravity Capture",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Status("Stage test failed ❌");
                    WpfMessageBox.Show($"Failed to post test event.\n\n{error}", "Gravity Capture",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                WpfMouse.OverrideCursor = null;
            }
        }

        private async void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            var raw = (LogLineBox.Text ?? "").Trim();
            var server = (ServerBox.Text ?? "").Trim();
            var tribe = (TribeBox.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(raw))
            {
                WpfMessageBox.Show("Paste a tribe log line first.", "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tribe))
            {
                WpfMessageBox.Show("Enter Server and Tribe (top of the window).", "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            var (okParse, evt, parseErr) = LogLineParser.TryParse(raw, server, tribe);
            if (!okParse || evt == null)
            {
                WpfMessageBox.Show($"Couldn't parse that line.\n\n{parseErr}", "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            WpfMouse.OverrideCursor = WpfCursors.Wait;
            Status("Posting parsed event…");
            try
            {
                var (ok, error) = await LogIngestClient.PostEventAsync(evt);
                if (ok)
                {
                    Status("Parsed event posted ✅");
                    WpfMessageBox.Show("Posted to staging API ✅", "Gravity Capture",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Status("Post failed ❌");
                    WpfMessageBox.Show($"Failed to post.\n\n{error}", "Gravity Capture",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                WpfMouse.OverrideCursor = null;
            }
        }

        private async void RefreshRecentBtn_Click(object sender, RoutedEventArgs e)
        {
            var server = (ServerBox.Text ?? "").Trim();
            var tribe = (TribeBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tribe))
            {
                WpfMessageBox.Show("Enter Server and Tribe (top of the window).", "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            WpfMouse.OverrideCursor = WpfCursors.Wait;
            Status("Loading recent events…");
            try
            {
                var (ok, items, err) = await LogIngestClient.GetRecentAsync(server, tribe, 25);
                if (!ok || items == null)
                {
                    Status("Load failed ❌");
                    WpfMessageBox.Show(err ?? "Unknown error.", "Gravity Capture",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                RecentGrid.ItemsSource = items;
                Status($"Loaded {items.Count} rows.");
            }
            finally
            {
                WpfMouse.OverrideCursor = null;
            }
        }

        // ---------- Target window resolution ----------

        /// Resolve a reasonable target when we haven't just captured from a known window.
        private IntPtr ResolveTargetWindow()
        {
            // Reuse the last crop window if we still have it and it’s valid
            if (_lastCropHwnd != IntPtr.Zero &&
                WindowUtil.TryGetClientBoundsOnScreen(_lastCropHwnd, out _, out _, out _, out _, out _))
            {
                return _lastCropHwnd;
            }

            var hint = _settings.TargetWindowHint?.Trim();
            if (!string.IsNullOrEmpty(hint))
            {
                var byTitle = WindowUtil.FindBestWindowByTitleHint(hint);
                if (byTitle != IntPtr.Zero) return byTitle;
            }
            return WindowUtil.GetForegroundWindow();
        }

        // ---------- Crop selection & preview ----------

        private async void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            // Make sure TitleHint and other UI are saved first
            SaveSettings();

            var dlg = new RegionSelectorWindow();
            var ok = dlg.ShowDialog() == true;
            if (!ok) return;

            // The region is in SCREEN coordinates. Use its center to find the top-level window beneath.
            var s = dlg.SelectedRect;
            int midX = (int)Math.Round(s.X + s.Width  / 2.0);
            int midY = (int)Math.Round(s.Y + s.Height / 2.0);
            var underSelection = WindowUtil.GetTopLevelWindowFromPoint(midX, midY);

            // Fallback: title-based or foreground
            var hwnd = underSelection != IntPtr.Zero ? underSelection : ResolveTargetWindow();
            _lastCropHwnd = hwnd;

            var chosenName = WindowUtil.GetWindowDebugName(hwnd);
            Status(string.IsNullOrWhiteSpace(chosenName) ? "Target window: <unknown>" : $"Target window: {chosenName}");

            if (!WindowUtil.TryGetClientBoundsOnScreen(hwnd, out int cx, out int cy, out int cw, out int ch, out _))
            {
                System.Windows.MessageBox.Show("Could not resolve game window client area.", "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Clamp selection to that window's client rect
            int sx = Math.Max(cx, Math.Min(cx + cw, (int)Math.Round(s.X)));
            int sy = Math.Max(cy, Math.Min(cy + ch, (int)Math.Round(s.Y)));
            int ex = Math.Max(cx, Math.Min(cx + cw, (int)Math.Round(s.X + s.Width)));
            int ey = Math.Max(cy, Math.Min(cy + ch, (int)Math.Round(s.Y + s.Height)));
            int selW = Math.Max(1, ex - sx);
            int selH = Math.Max(1, ey - sy);

            _settings.CropX = (double)(sx - cx) / cw;
            _settings.CropY = (double)(sy - cy) / ch;
            _settings.CropW = (double)selW / cw;
            _settings.CropH = (double)selH / ch;
            _settings.UseCrop = true;
            _settings.Save();

            Status($"Saved crop for '{chosenName}': x={_settings.CropX:F3} y={_settings.CropY:F3} w={_settings.CropW:F3} h={_settings.CropH:F3}");
            WpfMessageBox.Show("Saved crop. Click 'Preview Crop' to verify.", "Gravity Capture",
                MessageBoxButton.OK, MessageBoxImage.Information);
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private void PreviewCropBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();

            if (!_settings.UseCrop)
            {
                WpfMessageBox.Show("No crop saved. Click 'Select Log Area…' first.", "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            // Prefer the last crop window for this session; else normal resolution
            var hwnd = _lastCropHwnd != IntPtr.Zero ? _lastCropHwnd : ResolveTargetWindow();

            var chosenName = WindowUtil.GetWindowDebugName(hwnd);
            Status(string.IsNullOrWhiteSpace(chosenName) ? "Preview target: <unknown>" : $"Preview target: {chosenName}");

            try
            {
                using var bmp = ScreenCapture.CaptureCropNormalized(
                    hwnd, _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);

                var w = new Window { Title = "Crop Preview", Width = Math.Min(900, bmp.Width + 24), Height = Math.Min(700, bmp.Height + 48) };
                var img = new System.Windows.Controls.Image();
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit();
                img.Source = bi;

                w.Content = new System.Windows.Controls.ScrollViewer { Content = img };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show("Preview failed: " + ex.Message, "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------- NEW: OCR Crop → Paste ----------

        private void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            if (!_settings.UseCrop)
            {
                WpfMessageBox.Show("No crop saved. Click 'Select Log Area…' first.", "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            try
            {
                // Prefer the last crop window used this session so OCR matches your preview
                var hwnd = _lastCropHwnd != IntPtr.Zero ? _lastCropHwnd : ResolveTargetWindow();
                using var bmp = ScreenCapture.CaptureCropNormalized(
                    hwnd, _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);

                var lines = OcrService.ReadLines(bmp);
                if (lines.Count == 0)
                {
                    Status("OCR: no text detected.");
                    return;
                }

                // Paste OCR text into the big box so you can edit/test-send
                LogLineBox.Text = string.Join(Environment.NewLine, lines);
                Status($"OCR: {lines.Count} line(s). Review/edit then click 'Send Pasted Log Line (Stage)'.");
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show("OCR failed: " + ex.Message, "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
