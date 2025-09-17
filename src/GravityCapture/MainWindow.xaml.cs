using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions; // for picking the newest 'Day ...' line

using GravityCapture.Models;
using GravityCapture.Services;
using GravityCapture.Views;

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
        private readonly OcrIngestor _ingestor = new();
        private ApiClient? _api;

        private IntPtr _lastCropHwnd = IntPtr.Zero;

        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, __) => ApplyDarkTitleBar();

            _settings = AppSettings.Load();

            // --- env picker init ---
            EnvBox.SelectedIndex = _settings.LogEnvironment.Equals("Prod", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            LoadEnvFieldsIntoTextBoxes();

            // existing binds
            ChannelBox.Text    = _settings.ChannelId == 0 ? "" : _settings.ChannelId.ToString();
            IntervalBox.Text   = _settings.IntervalMinutes.ToString();
            ActiveWindowCheck.IsChecked = _settings.CaptureActiveWindow;
            QualitySlider.Value = _settings.JpegQuality;
            QualityLabel.Text   = _settings.JpegQuality.ToString();
            QualitySlider.ValueChanged += (_, __) => QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            TitleHintBox.Text  = _settings.TargetWindowHint;

            AutoOcrCheck.IsChecked = _settings.AutoOcrEnabled;
            RedOnlyCheck.IsChecked = _settings.PostOnlyCritical;

            FilterTameCheck.IsChecked   = _settings.FilterTameDeath;
            FilterStructCheck.IsChecked = _settings.FilterStructureDestroyed;
            FilterTribeCheck.IsChecked  = _settings.FilterTribeMateDeath;

            // configure log client for current env
            LogIngestClient.Configure(_settings);

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

        // ---------- env helpers ----------
        private string CurrentEnv => EnvBox.SelectedIndex == 1 ? "Prod" : "Stage";

        private void LoadEnvFieldsIntoTextBoxes()
        {
            _settings.LogEnvironment = CurrentEnv;
            var (url, key) = _settings.GetActiveLogApi();
            ApiUrlBox.Text = url ?? "";
            ApiKeyBox.Text = key ?? "";
        }

        private void SaveTextBoxesIntoEnvFields()
        {
            _settings.LogEnvironment = CurrentEnv;
            _settings.SetActiveLogApi(ApiUrlBox.Text?.TrimEnd('/') ?? "", ApiKeyBox.Text ?? "");
        }

        private void EnvBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SaveTextBoxesIntoEnvFields();
            _settings.LogEnvironment = CurrentEnv;
            LoadEnvFieldsIntoTextBoxes();
            LogIngestClient.Configure(_settings);
            Status($"Switched log environment → {CurrentEnv}");
        }

        // ---------- Save/Start/Stop ----------
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
            SaveTextBoxesIntoEnvFields();      // save env-specific url/key
            _settings.ChannelId = ulong.TryParse(ChannelBox.Text, out var ch) ? ch : 0;
            _settings.IntervalMinutes = int.TryParse(IntervalBox.Text, out var m) ? Math.Max(1, m) : 1;
            _settings.CaptureActiveWindow = ActiveWindowCheck.IsChecked == true;
            _settings.JpegQuality = (int)QualitySlider.Value;
            _settings.TargetWindowHint = TitleHintBox.Text ?? "";

            _settings.AutoOcrEnabled   = AutoOcrCheck.IsChecked == true;
            _settings.PostOnlyCritical = RedOnlyCheck.IsChecked == true;

            _settings.FilterTameDeath          = FilterTameCheck.IsChecked == true;
            _settings.FilterStructureDestroyed = FilterStructCheck.IsChecked == true;
            _settings.FilterTribeMateDeath     = FilterTribeCheck.IsChecked == true;

            _settings.Save();

            LogIngestClient.Configure(_settings); // ensure client uses latest env/keys
        }

        private void StartCapture()
        {
            // Note: your screenshot API (if used) still reads ApiUrl/ApiKey.
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

        // ---------- Timer ----------
        private async void OnTick(object? s, ElapsedEventArgs e)
        {
            try
            {
                // Snapshot UI values on the UI thread to avoid cross-thread access
                var (server, tribe) = await Dispatcher.InvokeAsync(() =>
                {
                    var srv = ServerBox.Text?.Trim() ?? "";
                    var trb = TribeBox.Text?.Trim() ?? "";
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
                // Never allow the timer thread to crash/freeze the app
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
            catch (Exception ex)
            {
                Status("Error: " + ex.Message);
            }
        }

        private void Status(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

        // ---------- Manual stage/prod test / paste / recent ----------
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
            var raw   = (LogLineBox.Text ?? "").Trim();
            var server= (ServerBox.Text ?? "").Trim();
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
            var server= (ServerBox.Text ?? "").Trim();
            var tribe = (TribeBox.Text ?? "").Trim();
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
                RecentGrid.ItemsSource = items;
                Status($"Loaded {items.Count} rows.");
            }
            finally { WpfMouse.OverrideCursor = null; }
        }

        // ---------- Window targeting, crop, preview, OCR ----------
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

            var s = dlg.SelectedRect; // screen coords
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

            // clamp selection
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

                w.Content = new System.Windows.Controls.ScrollViewer { Content = img };
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

        // ---------- One-click OCR & post newest visible line ----------
        private async void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings(); // persist current UI values & ensure client/env are configured

            if (!_settings.UseCrop)
            {
                WpfMessageBox.Show(
                    "No crop saved. Click 'Select Log Area…' first.",
                    "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            var server = (ServerBox.Text ?? "").Trim();
            var tribe  = (TribeBox.Text  ?? "").Trim();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tribe))
            {
                WpfMessageBox.Show(
                    "Enter Server and Tribe (top of the window) before posting.",
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

                // Pick the newest visible log line: topmost line that starts with "Day <digits>"
                // Fallback: first non-empty line.
                var reDay = new Regex(@"^\s*Day\s+\d+", RegexOptions.IgnoreCase);
                var candidate = lines.Find(l => reDay.IsMatch(l)) ?? lines.Find(l => !string.IsNullOrWhiteSpace(l));

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    Status("OCR: couldn't find a 'Day ...' line.");
                    WpfMessageBox.Show(
                        "Couldn't find a line starting with 'Day ...' in the OCR result.",
                        "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Put it in the textbox so you can see exactly what we’re posting
                LogLineBox.Text = candidate;

                // Parse
                var (okParse, evt, parseErr) = LogLineParser.TryParse(candidate, server, tribe);
                if (!okParse || evt == null)
                {
                    Status("Parse failed.");
                    WpfMessageBox.Show(
                        $"Couldn't parse the OCR’d line:\n\n{candidate}\n\n{parseErr}",
                        "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Apply “Post only red logs” filter if enabled (null-safe)
                if (_settings.PostOnlyCritical &&
                    !string.Equals(GetSeverityValue(evt) ?? "", "CRITICAL", StringComparison.OrdinalIgnoreCase))
                {
                    Status("Filtered (not critical).");
                    WpfMessageBox.Show(
                        "Parsed line is not CRITICAL and was filtered by your settings.",
                        "Gravity Capture", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Post to selected env (client is already configured by SaveSettings/Env switch)
                Status($"Posting to {(_settings.LogEnvironment ?? "Stage")}…");
                var (ok, error) = await LogIngestClient.PostEventAsync(evt);

                if (ok)
                {
                    Status("Posted ✅");
                    WpfMessageBox.Show(
                        $"Posted to {(_settings.LogEnvironment ?? "Stage")} ✅",
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

        // -------- helpers (inside class, outside other methods) --------

        private static string? TryGetStringProp(object obj, string name)
        {
            var p = obj.GetType().GetProperty(
                name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.IgnoreCase);
            return p?.GetValue(obj)?.ToString();
        }

        /// <summary>
        /// Returns the severity/level string from a TribeEvent-like object,
        /// regardless of property casing/name.
        /// </summary>
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
    } // end class
} // end namespace
