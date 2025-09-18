using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

using GravityCapture.Models;          // AppSettings, TribeEvent models
using GravityCapture.Services;        // OcrService, LogIngestClient, ScreenCapture, OcrIngestor
using GravityCapture.Views;           // RegionSelectorWindow (if present)

using WpfMessageBox = System.Windows.MessageBox;
using WpfCursors    = System.Windows.Input.Cursors;
using WpfMouse      = System.Windows.Input.Mouse;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        // --- Dark title bar support (cosmetic) ---
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_NEW = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private readonly AppSettings _settings;
        private readonly System.Timers.Timer _timer;
        private readonly OcrIngestor _ingestor = new();
        private ApiClient? _api;

        // Remember the last window we cropped from so we don’t lose the handle
        private IntPtr _lastCropHwnd = IntPtr.Zero;

        public MainWindow()
        {
            InitializeComponent();

            // Optional dark title bar
            SourceInitialized += (_, __) => ApplyDarkTitleBar();

            // Load persisted settings
            _settings = AppSettings.Load();

            // --- hydrate UI from settings ---
            EnvBox.SelectedIndex = _settings.LogEnvironment.Equals("Prod", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            LoadEnvFieldsIntoTextBoxes(); // sets ApiUrlBox/ApiKeyBox based on env

            ChannelBox.Text  = _settings.ChannelId == 0 ? "" : _settings.ChannelId.ToString();
            IntervalBox.Text = _settings.IntervalMinutes.ToString();
            ActiveWindowCheck.IsChecked = _settings.CaptureActiveWindow;

            QualitySlider.Value = _settings.JpegQuality;
            QualityLabel.Text   = _settings.JpegQuality.ToString();
            QualitySlider.ValueChanged += (_, __) => QualityLabel.Text = ((int)QualitySlider.Value).ToString();

            TitleHintBox.Text = _settings.TargetWindowHint ?? string.Empty; // harmless to keep even if unused
            ServerBox.Text    = _settings.ServerName ?? string.Empty;
            TribeBox.Text     = _settings.TribeName  ?? string.Empty;

            AutoOcrCheck.IsChecked = _settings.AutoOcrEnabled;
            RedOnlyCheck.IsChecked = _settings.PostOnlyCritical;

            FilterTameCheck.IsChecked   = _settings.FilterTameDeath;
            FilterStructCheck.IsChecked = _settings.FilterStructureDestroyed;
            FilterTribeCheck.IsChecked  = _settings.FilterTribeMateDeath;

            // Prepare HTTP client with current env
            LogIngestClient.Configure(_settings);

            // Timer
            _timer = new System.Timers.Timer { AutoReset = true, Enabled = false };
            _timer.Elapsed += OnTick;

            // ALWAYS persist on exit (fixes “fields reset on reopen”)
            Closing += (_, __) =>
            {
                try { SaveSettings(); } catch { /* don’t block closing */ }
            };

            if (_settings.Autostart)
                StartCapture();
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
            catch { /* cosmetic only */ }
        }

        // ========== ENVIRONMENT ==========
        private string CurrentEnv => EnvBox.SelectedIndex == 1 ? "Prod" : "Stage";

        private void LoadEnvFieldsIntoTextBoxes()
        {
            _settings.LogEnvironment = CurrentEnv;
            var (url, key) = _settings.GetActiveLogApi();
            ApiUrlBox.Text = url ?? string.Empty;
            ApiKeyBox.Text = key ?? string.Empty;
        }

        private void SaveTextBoxesIntoEnvFields()
        {
            _settings.LogEnvironment = CurrentEnv;
            _settings.SetActiveLogApi(ApiUrlBox.Text?.TrimEnd('/') ?? string.Empty, ApiKeyBox.Text ?? string.Empty);
        }

        private void EnvBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SaveTextBoxesIntoEnvFields();
            _settings.LogEnvironment = CurrentEnv;
            LoadEnvFieldsIntoTextBoxes();
            LogIngestClient.Configure(_settings);
            Status($"Switched log environment → {CurrentEnv}");
        }

        // ========== SAVE / START / STOP ==========
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
            // Persist env URL & key
            SaveTextBoxesIntoEnvFields();

            _settings.ChannelId           = ulong.TryParse(ChannelBox.Text, out var ch) ? ch : 0;
            _settings.IntervalMinutes     = int.TryParse(IntervalBox.Text, out var m) ? Math.Max(1, m) : 1;
            _settings.CaptureActiveWindow = ActiveWindowCheck.IsChecked == true;
            _settings.JpegQuality         = (int)QualitySlider.Value;
            _settings.TargetWindowHint    = TitleHintBox.Text ?? string.Empty;

            _settings.ServerName = ServerBox.Text?.Trim() ?? string.Empty;
            _settings.TribeName  = TribeBox.Text?.Trim()  ?? string.Empty;

            _settings.AutoOcrEnabled       = AutoOcrCheck.IsChecked == true;
            _settings.PostOnlyCritical     = RedOnlyCheck.IsChecked == true;
            _settings.FilterTameDeath          = FilterTameCheck.IsChecked   == true;
            _settings.FilterStructureDestroyed = FilterStructCheck.IsChecked == true;
            _settings.FilterTribeMateDeath     = FilterTribeCheck.IsChecked  == true;

            _settings.Save();
            LogIngestClient.Configure(_settings);
        }

        private void StartCapture()
        {
            _api = new ApiClient(_settings.ApiUrl, _settings.ApiKey);
            _timer.Interval = TimeSpan.FromMinutes(_settings.IntervalMinutes).TotalMilliseconds;
            _timer.Start();
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled  = true;
            Status($"Running – every {_settings.IntervalMinutes} min.");
        }

        private void StopCapture()
        {
            _timer.Stop();
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled  = false;
            Status("Stopped.");
        }

        // ========== TIMER ==========
        private async void OnTick(object? s, ElapsedEventArgs e)
        {
            try
            {
                var (server, tribe) = await Dispatcher.InvokeAsync(() =>
                {
                    var srv = ServerBox.Text?.Trim() ?? string.Empty;
                    var trb = TribeBox.Text?.Trim() ?? string.Empty;
                    return (srv, trb);
                });

                if (_settings.AutoOcrEnabled)
                {
                    var hwnd = ResolveTargetWindow();
                    await _ingestor.ScanAndPostAsync(
                        hwnd,
                        _settings,
                        server,
                        tribe,
                        async msg => await Dispatcher.InvokeAsync(() => Status(msg))
                    );
                }
                else
                {
                    await CaptureOnceAsync();
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => Status($"Timer error: {ex.Message}"));
            }
        }

        private async System.Threading.Tasks.Task CaptureOnceAsync()
        {
            if (_api == null) return;
            try
            {
                System.Drawing.Bitmap bmp;
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
            catch (Exception ex) { Status("Error: " + ex.Message); }
        }

        private void Status(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

        // ========== MANUAL POSTS ==========
        private async void SendTestBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            WpfMouse.OverrideCursor = WpfCursors.Wait;
            Status($"Posting test to {CurrentEnv}…");
            try
            {
                var (ok, error) = await LogIngestClient.SendTestAsync();
                if (ok)
                {
                    Status("Test event posted ✅");
                    WpfMessageBox.Show($"Posted to {CurrentEnv} ✅", "Gravity Capture",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Status("Test failed ❌");
                    WpfMessageBox.Show($"Failed to post.\n\n{error}", "Gravity Capture",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally { WpfMouse.OverrideCursor = null; }
        }

        private async void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            var raw    = (LogLineBox.Text ?? string.Empty).Trim();
            var server = (ServerBox.Text ?? string.Empty).Trim();
            var tribe  = (TribeBox.Text  ?? string.Empty).Trim();

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
            Status($"Posting parsed event to {CurrentEnv}…");
            try
            {
                var (ok, error) = await LogIngestClient.PostEventAsync(evt);
                if (ok)
                {
                    Status("Parsed event posted ✅");
                    WpfMessageBox.Show($"Posted to {CurrentEnv} ✅", "Gravity Capture",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Status("Post failed ❌");
                    WpfMessageBox.Show($"Failed to post.\n\n{error}", "Gravity Capture",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally { WpfMouse.OverrideCursor = null; }
        }

        private async void RefreshRecentBtn_Click(object sender, RoutedEventArgs e)
        {
            var server = (ServerBox.Text ?? string.Empty).Trim();
            var tribe  = (TribeBox.Text  ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tribe))
            {
                WpfMessageBox.Show("Enter Server and Tribe (top of the window).", "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            WpfMouse.OverrideCursor = WpfCursors.Wait;
            Status($"Loading recent events from {CurrentEnv}…");
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

                // Works even if XAML doesn’t have a generated field
                if (FindName("RecentGrid") is DataGrid grid)
                    grid.ItemsSource = items;

                Status($"Loaded {items.Count} rows.");
            }
            finally { WpfMouse.OverrideCursor = null; }
        }

        // ========== WINDOW TARGETING & OCR ==========
        private IntPtr ResolveTargetWindow()
        {
            if (_lastCropHwnd != IntPtr.Zero &&
                WindowUtil.TryGetClientBoundsOnScreen(_lastCropHwnd, out _, out _, out _, out _, out _))
                return _lastCropHwnd;

            var hint = _settings.TargetWindowHint?.Trim();
            if (!string.IsNullOrEmpty(hint))
            {
                var byTitle = WindowUtil.FindBestWindowByTitleHint(hint);
                if (byTitle != IntPtr.Zero) return byTitle;
            }
            return WindowUtil.GetForegroundWindow();
        }

        private async void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();

            var dlg = new RegionSelectorWindow();
            var ok = dlg.ShowDialog() == true;
            if (!ok) return;

            var s = dlg.SelectedRect; // screen-coordinates selection
            int midX = (int)Math.Round(s.X + s.Width  / 2.0);
            int midY = (int)Math.Round(s.Y + s.Height / 2.0);
            var underSelection = WindowUtil.GetTopLevelWindowFromPoint(midX, midY);

            var hwnd = underSelection != IntPtr.Zero ? underSelection : ResolveTargetWindow();
            _lastCropHwnd = hwnd;

            var chosenName = WindowUtil.GetWindowDebugName(hwnd);
            Status(string.IsNullOrWhiteSpace(chosenName) ? "Target window: <unknown>" : $"Target window: {chosenName}");

            if (!WindowUtil.TryGetClientBoundsOnScreen(hwnd, out int cx, out int cy, out int cw, out int ch, out _))
            {
                WpfMessageBox.Show("Could not resolve game window client area.", "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // clamp to client rect
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

                w.Content = new ScrollViewer { Content = img };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show("Preview failed: " + ex.Message, "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

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
                var hwnd = _lastCropHwnd != IntPtr.Zero ? _lastCropHwnd : ResolveTargetWindow();
                using var bmp = ScreenCapture.CaptureCropNormalized(
                    hwnd, _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);

                var lines = OcrService.ReadLines(bmp);
                if (lines.Count == 0)
                {
                    Status("OCR: no text detected.");
                    return;
                }

                LogLineBox.Text = string.Join(Environment.NewLine, lines);
                Status($"OCR: {lines.Count} line(s). Review/edit then click 'Send Pasted Log Line (Stage/Prod)'.");
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show("OCR failed: " + ex.Message, "Gravity Capture",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();

            if (!_settings.UseCrop)
            {
                WpfMessageBox.Show("No crop saved. Click 'Select Log Area…' first.",
                    "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            var server = (ServerBox.Text ?? string.Empty).Trim();
            var tribe  = (TribeBox.Text  ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tribe))
            {
                WpfMessageBox.Show("Enter Server and Tribe (top of the window) before posting.",
                    "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            try
            {
                WpfMouse.OverrideCursor = WpfCursors.Wait;
                Status("OCR: capturing…");

                var hwnd = _lastCropHwnd != IntPtr.Zero ? _lastCropHwnd : ResolveTargetWindow();

                using var bmp = ScreenCapture.CaptureCropNormalized(
                    hwnd, _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);

                var lines = OcrService.ReadLines(bmp);
                if (lines.Count == 0)
                {
                    Status("OCR: no text detected.");
                    WpfMessageBox.Show("OCR returned no text. Try a tighter crop or adjust brightness/contrast.",
                        "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Prefer a “Day …” line
                var reDay    = new Regex(@"^\s*Day\s+\d+", RegexOptions.IgnoreCase);
                var candidate= lines.Find(l => reDay.IsMatch(l)) ?? lines.Find(l => !string.IsNullOrWhiteSpace(l));

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    Status("OCR: couldn't find a 'Day ...' line.");
                    WpfMessageBox.Show("Couldn't find a line starting with 'Day ...' in the OCR result.",
                        "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                LogLineBox.Text = candidate;

                var (okParse, evt, parseErr) = LogLineParser.TryParse(candidate, server, tribe);
                if (!okParse || evt == null)
                {
                    Status("Parse failed.");
                    WpfMessageBox.Show($"Couldn't parse the OCR’d line:\n\n{candidate}\n\n{parseErr}",
                        "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Respect the “Post only red logs” toggle
                if (_settings.PostOnlyCritical &&
                    !string.Equals(GetSeverityValue(evt) ?? "", "CRITICAL", StringComparison.OrdinalIgnoreCase))
                {
                    Status("Filtered (not critical).");
                    WpfMessageBox.Show("Parsed line is not CRITICAL and was filtered by your settings.",
                        "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Status($"Posting to {(_settings.LogEnvironment ?? "Stage")}…");
                var (ok, error) = await LogIngestClient.PostEventAsync(evt);

                if (ok)
                {
                    Status("Posted ✅");
                    WpfMessageBox.Show($"Posted to {(_settings.LogEnvironment ?? "Stage")} ✅",
                        "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Status("Post failed ❌");
                    WpfMessageBox.Show($"Post failed.\n\n{error}",
                        "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Status("Error: " + ex.Message);
                WpfMessageBox.Show("OCR & Post failed:\n\n" + ex.Message,
                    "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                WpfMouse.OverrideCursor = null;
            }
        }

        // ========== helpers ==========
        private static string? GetSeverityValue(object evt)
        {
            var t = evt.GetType();
            var flags = System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.IgnoreCase;

            var p = t.GetProperty("Severity", flags)
                 ?? t.GetProperty("Level", flags)
                 ?? t.GetProperty("severity", flags)
                 ?? t.GetProperty("level",   flags);

            var val = p?.GetValue(evt)?.ToString();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        // ========== XAML WRAPPERS ==========
        // These keep your existing XAML handler names working.
        // Once you rename the XAML to the *Btn_Click names, you can delete these.
        private void RefreshRecent_Click(object sender, RoutedEventArgs e)       => RefreshRecentBtn_Click(sender, e);
        private void SelectCropArea_Click(object sender, RoutedEventArgs e)      => SelectCropBtn_Click(sender, e);
        private void PreviewCrop_Click(object sender, RoutedEventArgs e)         => PreviewCropBtn_Click(sender, e);
        private void OcrCropPaste_Click(object sender, RoutedEventArgs e)        => OcrCropBtn_Click(sender, e);
        private void OcrAndPostNow_Click(object sender, RoutedEventArgs e)       => OcrAndPostNowBtn_Click(sender, e);
        private void SendTestTribeEvent_Click(object sender, RoutedEventArgs e)  => SendTestBtn_Click(sender, e);
        private void SendPastedLine_Click(object sender, RoutedEventArgs e)      => SendParsedBtn_Click(sender, e);

        // ========== window resolution ==========
        private IntPtr ResolveTopLevelFromPoint(int x, int y) =>
            WindowUtil.GetTopLevelWindowFromPoint(x, y);
    }
}
