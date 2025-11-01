using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GravityCapture.Models;
using GravityCapture.Services;
using GravityCapture.Views;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private const double Aspect = 16.0 / 10.0;

        private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

        private readonly string _settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GravityCapture");

        private readonly string _settingsFile;
        private readonly string _overlayFile;

        private AppSettings _settings = AppSettings.Load();
        private DispatcherTimer? _timer;
        private System.Drawing.Rectangle? _selectedScreenRect;
        private IntPtr _arkHwnd = IntPtr.Zero;
        private bool _showOcrOverlay;

        public MainWindow()
        {
            InitializeComponent();

            _settingsFile = Path.Combine(_settingsDir, "global.json");
            _overlayFile = Path.Combine(_settingsDir, "overlay.json");
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettingsIntoUi();
            LoadOverlayPreference();
            ShowCapturePage();

            StartArkPolling();
            SetStatus("Ready.");
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            TrySaveFromUi();
            SaveOverlayPreference();
        }

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int useDark = 1;
                _ = DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
            }
            catch { }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                var minH = Math.Max(MinHeight, e.NewSize.Width / Aspect);
                if (Height < minH) Height = minH;
            }
            else if (e.HeightChanged)
            {
                var minW = Math.Max(MinWidth, e.NewSize.Height * Aspect);
                if (Width < minW) Width = minW;
            }
        }

        // ================= settings =================

        private void LoadSettingsIntoUi()
        {
            ChannelBox.Text = _settings.Image?.ChannelId ?? "";
            ApiUrlBox.Text = _settings.ApiBaseUrl ?? "";
            ApiKeyBox.Password = _settings.Auth?.ApiKey ?? "";
            ServerBox.Text = _settings.Capture?.ServerName ?? _settings.ServerName ?? "";
            TribeBox.Text = _settings.TribeName ?? "";

            OcrPathBox.Text = _settings.OcrPath ?? "";
            ScreenshotPathBox.Text = _settings.ScreenshotIngestPath ?? "";
            LogLinePathBox.Text = _settings.LogLineIngestPath ?? "";

            var f = _settings.Filters ?? new AppSettings.FilterSettings();
            FilterTameDeathsBox.IsChecked = f.TameDeaths;
            FilterTamesStarvingBox.IsChecked = f.TamesStarved;
            FilterStructDestroyedBox.IsChecked = f.StructuresDestroyed;
            FilterStructAutoDecayBox.IsChecked = f.StructuresAutoDecay;
            FilterTribeMateDeathsBox.IsChecked = f.TribemateDeaths;
            FilterTribeKillsEnemyTamesBox.IsChecked = f.TribeKillsEnemyTames;
            FilterEnemyPlayerKillsBox.IsChecked = f.EnemyPlayerKills;
            FilterTribeDemolishBox.IsChecked = f.TribematesDemolishing;
            FilterTribeFreezeTamesBox.IsChecked = f.TribematesFreezingTames;

            if (_settings.UseCrop && _settings.CropW > 0 && _settings.CropH > 0)
                _selectedScreenRect = new System.Drawing.Rectangle(_settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);
        }

        private bool TrySaveFromUi()
        {
            try
            {
                _settings.Image ??= new AppSettings.ImageSettings();
                _settings.Auth ??= new AppSettings.AuthSettings();
                _settings.Capture ??= new AppSettings.CaptureSettings();
                _settings.Filters ??= new AppSettings.FilterSettings();

                _settings.Image.ChannelId = ChannelBox.Text?.Trim();
                _settings.ApiBaseUrl = ApiUrlBox.Text?.Trim();
                _settings.Auth.ApiKey = ApiKeyBox.Password?.Trim();
                _settings.Capture.ServerName = string.IsNullOrWhiteSpace(ServerBox.Text) ? null : ServerBox.Text.Trim();
                _settings.TribeName = string.IsNullOrWhiteSpace(TribeBox.Text) ? null : TribeBox.Text.Trim();

                _settings.OcrPath = string.IsNullOrWhiteSpace(OcrPathBox.Text) ? null : OcrPathBox.Text.Trim();
                _settings.ScreenshotIngestPath = string.IsNullOrWhiteSpace(ScreenshotPathBox.Text) ? null : ScreenshotPathBox.Text.Trim();
                _settings.LogLineIngestPath = string.IsNullOrWhiteSpace(LogLinePathBox.Text) ? null : LogLinePathBox.Text.Trim();

                _settings.Filters.TameDeaths = FilterTameDeathsBox.IsChecked == true;
                _settings.Filters.TamesStarved = FilterTamesStarvingBox.IsChecked == true;
                _settings.Filters.StructuresDestroyed = FilterStructDestroyedBox.IsChecked == true;
                _settings.Filters.StructuresAutoDecay = FilterStructAutoDecayBox.IsChecked == true;
                _settings.Filters.TribemateDeaths = FilterTribeMateDeathsBox.IsChecked == true;
                _settings.Filters.TribeKillsEnemyTames = FilterTribeKillsEnemyTamesBox.IsChecked == true;
                _settings.Filters.EnemyPlayerKills = FilterEnemyPlayerKillsBox.IsChecked == true;
                _settings.Filters.TribematesDemolishing = FilterTribeDemolishBox.IsChecked == true;
                _settings.Filters.TribematesFreezingTames = FilterTribeFreezeTamesBox.IsChecked == true;

                Directory.CreateDirectory(_settingsDir);
                File.WriteAllText(_settingsFile, JsonSerializer.Serialize(_settings, _json));

                return true;
            }
            catch (Exception ex)
            {
                SetStatus($"Save failed: {ex.Message}");
                return false;
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (TrySaveFromUi())
                SetStatus("Saved.");
        }

        // ================ overlay preference (separate tiny file) ================
        private sealed class OverlayDto { public bool Show { get; set; } }

        private void LoadOverlayPreference()
        {
            try
            {
                if (File.Exists(_overlayFile))
                {
                    var dto = JsonSerializer.Deserialize<OverlayDto>(File.ReadAllText(_overlayFile));
                    _showOcrOverlay = dto?.Show ?? false;
                }
            }
            catch { _showOcrOverlay = false; }

            ShowOcrDetailsCheck.IsChecked = _showOcrOverlay;
            OcrDetailsOverlay.Visibility = _showOcrOverlay ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveOverlayPreference()
        {
            try
            {
                Directory.CreateDirectory(_settingsDir);
                var dto = new OverlayDto { Show = _showOcrOverlay };
                File.WriteAllText(_overlayFile, JsonSerializer.Serialize(dto, _json));
            }
            catch { }
        }

        // ================= ARK window polling =================
        private void StartArkPolling()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, __) =>
            {
                (_arkHwnd, var title) = FindArkWindow();
                if (_arkHwnd != IntPtr.Zero)
                    StatusText.Text = $"ARK window: {title}";
            };
            timer.Start();
        }

        private (IntPtr hwnd, string title) FindArkWindow()
        {
            IntPtr found = IntPtr.Zero;
            string title = "";
            var target = _settings.TargetWindowHint ?? "ARK";

            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                var t = GetWindowText(h);
                if (!string.IsNullOrWhiteSpace(t) && t.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = h;
                    title = t;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return (found, title);
        }

        // ================= Buttons =================

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedScreenRect == null) { SetStatus("Select log area first."); return; }

            _timer ??= new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(_settings.IntervalMinutes > 0 ? _settings.IntervalMinutes * 60_000 : 2500);
            _timer.Tick -= OnPreviewTick;
            _timer.Tick += OnPreviewTick;
            _timer.Start();
            SetStatus("Started.");
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _timer?.Stop();
            SetStatus("Stopped.");
        }

        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            var overlay = new RegionSelectorWindow(_arkHwnd);
            if (overlay.ShowDialog() == true)
            {
                _selectedScreenRect = overlay.SelectedRect;

                _settings.UseCrop = true;
                _settings.CropX = _selectedScreenRect.Value.X;
                _settings.CropY = _selectedScreenRect.Value.Y;
                _settings.CropW = _selectedScreenRect.Value.Width;
                _settings.CropH = _selectedScreenRect.Value.Height;
                TrySaveFromUi();

                SetStatus($"Area set: {_selectedScreenRect.Value.Width}×{_selectedScreenRect.Value.Height} @ {_selectedScreenRect.Value.X},{_selectedScreenRect.Value.Y}");
            }
        }

        private async void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedScreenRect == null) { SetStatus("Select log area first."); return; }
            try
            {
                using var bmp = CaptureScreenRect(_selectedScreenRect.Value);
                LivePreview.Source = BmpToSource(bmp);

                var jpeg = EncodeJpeg(bmp, 100);
                using var api = new ProbingApiClient(_settings);
                var (ok, body) = await api.OcrOnlyAsync(jpeg);
                ApiEchoText.Text = body;

                if (!ok)
                {
                    using var ms = new MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    var remote = new RemoteOcrService(_settings);
                    var resp = await remote.ExtractAsync(ms, default);
                    var textJoined = resp?.TextJoined ?? "";
                    body = string.IsNullOrWhiteSpace(textJoined) ? "{\"error\":\"OCR failed\"}" : JsonSerializer.Serialize(resp);
                    ok = !string.IsNullOrWhiteSpace(textJoined);
                    ApiEchoText.Text = body;
                }

                if (ok)
                {
                    var text = TryExtractText(body);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        LogLineBox.Text = text;
                        if (_showOcrOverlay)
                        {
                            OcrDetailsText.Text = text;
                            OcrDetailsOverlay.Visibility = Visibility.Visible;
                        }
                    }
                    SetStatus("OCR returned");
                }
                else
                {
                    SetStatus("OCR failed");
                }
            }
            catch (Exception ex)
            {
                ApiEchoText.Text = ex.Message;
                SetStatus("OCR error: " + ex.Message);
            }
        }

        private async void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedScreenRect == null) { SetStatus("Select log area first."); return; }
            try
            {
                using var bmp = CaptureScreenRect(_selectedScreenRect.Value);
                LivePreview.Source = BmpToSource(bmp);

                var jpeg = EncodeJpeg(bmp, 100);
                using var api = new ProbingApiClient(_settings);
                var (ok, body) = await api.PostScreenshotAsync(jpeg, postVisible: true);
                ApiEchoText.Text = body;
                SetStatus(ok ? "Posted screenshot" : "Post failed");
            }
            catch (Exception ex)
            {
                ApiEchoText.Text = ex.Message;
                SetStatus("Post error: " + ex.Message);
            }
        }

        private async void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            var line = LogLineBox.Text?.Trim();
            if (string.IsNullOrEmpty(line)) { SetStatus("No text to send."); return; }
            try
            {
                using var api = new ProbingApiClient(_settings);
                var (ok, body) = await api.SendPastedLineAsync(line);
                ApiEchoText.Text = body;
                SetStatus(ok ? "Pasted line sent" : "Send failed");
            }
            catch (Exception ex)
            {
                ApiEchoText.Text = ex.Message;
                SetStatus("Send error: " + ex.Message);
            }
        }

        // XAML compatibility
        private void SendPastedBtn_Click(object sender, RoutedEventArgs e) => SendParsedBtn_Click(sender, e);

        private void ShowOcrDetailsCheck_Changed(object sender, RoutedEventArgs e)
        {
            _showOcrOverlay = ShowOcrDetailsCheck.IsChecked == true;
            OcrDetailsOverlay.Visibility = _showOcrOverlay ? Visibility.Visible : Visibility.Collapsed;
            if (!_showOcrOverlay)
                OcrDetailsText.Text = "";

            SaveOverlayPreference();
        }

        private void SaveDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfd = new SaveFileDialog { Filter = "ZIP (*.zip)|*.zip", FileName = "gravity-capture-debug.zip" };
                if (sfd.ShowDialog() != true) return;

                using var zip = ZipFile.Open(sfd.FileName, ZipArchiveMode.Create);
                var settingsEntry = zip.CreateEntry("settings.json");
                using (var sw = new StreamWriter(settingsEntry.Open())) sw.Write(JsonSerializer.Serialize(_settings, _json));
                var textEntry = zip.CreateEntry("last_text.txt");
                using (var sw = new StreamWriter(textEntry.Open())) sw.Write(LogLineBox.Text ?? "");
                var cropEntry = zip.CreateEntry("crop.txt");
                using (var sw = new StreamWriter(cropEntry.Open()))
                    sw.Write(_selectedScreenRect.HasValue
                        ? $"{_selectedScreenRect.Value.X},{_selectedScreenRect.Value.Y},{_selectedScreenRect.Value.Width},{_selectedScreenRect.Value.Height}"
                        : "unset");
                if (_selectedScreenRect != null)
                {
                    using var bmp = CaptureScreenRect(_selectedScreenRect.Value);
                    var imgEntry = zip.CreateEntry("preview.png");
                    using var zs = imgEntry.Open();
                    bmp.Save(zs, System.Drawing.Imaging.ImageFormat.Png);
                }
                SetStatus("Debug ZIP saved.");
            }
            catch (Exception ex)
            {
                SetStatus("ZIP error: " + ex.Message);
            }
        }

        // ================= preview timer =================
        private void OnPreviewTick(object? sender, EventArgs e)
        {
            if (_selectedScreenRect == null) return;
            try
            {
                using var bmp = CaptureScreenRect(_selectedScreenRect.Value);
                LivePreview.Source = BmpToSource(bmp);
            }
            catch (Exception ex)
            {
                SetStatus("Preview error: " + ex.Message);
            }
        }

        // ================= page switching =================
        private void ShowCapturePage()
        {
            CapturePage.Visibility = Visibility.Visible;
            SettingsPage.Visibility = Visibility.Collapsed;
            CaptureToolbar.Visibility = Visibility.Visible;

            CaptureTabButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FF3A3F47");
            SettingsTabButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FF2E333A");
        }

        private void ShowSettingsPage()
        {
            CapturePage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
            CaptureToolbar.Visibility = Visibility.Collapsed;

            CaptureTabButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FF2E333A");
            SettingsTabButton.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FF3A3F47");
        }

        private void CaptureTabButton_Click(object sender, RoutedEventArgs e) => ShowCapturePage();

        private void SettingsTabButton_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

        // ================= helpers =================
        private static Bitmap CaptureScreenRect(System.Drawing.Rectangle r)
        {
            var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height),
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(new System.Drawing.Point(r.X, r.Y), System.Drawing.Point.Empty, r.Size,
                System.Drawing.CopyPixelOperation.SourceCopy);
            return bmp;
        }

        private static byte[] EncodeJpeg(Bitmap bmp, int quality)
        {
            using var ms = new MemoryStream();
            var encoder = GetJpegEncoder();
            var eps = new System.Drawing.Imaging.EncoderParameters(1);
            eps.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality,
                Math.Clamp(quality, 1, 100));
            bmp.Save(ms, encoder, eps);
            return ms.ToArray();
        }

        private static System.Drawing.Imaging.ImageCodecInfo GetJpegEncoder()
        {
            foreach (var c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                if (c.MimeType == "image/jpeg") return c;
            throw new InvalidOperationException("JPEG encoder not found");
        }

        private static BitmapSource BmpToSource(Bitmap bmp)
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

        private static string TryExtractText(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("text", out var t)) return t.GetString() ?? body;
                if (doc.RootElement.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var ln in lines.EnumerateArray())
                        if (ln.TryGetProperty("text", out var lt)) sb.AppendLine(lt.GetString());
                    return sb.ToString().Trim();
                }
            }
            catch { }
            return body;
        }

        private void SetStatus(string s) => StatusText.Text = s;

        // Win32
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        private static string GetWindowText(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void ApiEchoText_TextChanged(object sender, TextChangedEventArgs e) { }
        private void ServerBox_TextChanged(object sender, TextChangedEventArgs e) { }
        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void TribeBox_TextChanged(object sender, TextChangedEventArgs e) { }
    }

    internal sealed class ProbingApiClient : IDisposable
    {
        private readonly ApiClient2 _client;
        public ProbingApiClient(AppSettings s) { _client = new ApiClient2(s); }
        public System.Threading.Tasks.Task<(bool ok, string body)> OcrOnlyAsync(byte[] jpeg) => _client.OcrOnlyAsync(jpeg);
        public System.Threading.Tasks.Task<(bool ok, string body)> PostScreenshotAsync(byte[] jpeg, bool postVisible) => _client.PostScreenshotAsync(jpeg, postVisible);
        public System.Threading.Tasks.Task<(bool ok, string body)> SendPastedLineAsync(string line) => _client.SendPastedLineAsync(line);
        public void Dispose() => _client.Dispose();
    }
}
