using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        private readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GravityCapture");
        private readonly string SettingsFile;

        private AppSettings _settings = AppSettings.Load();
        private DispatcherTimer? _timer;
        private System.Drawing.Rectangle? _selectedScreenRect;
        private IntPtr _arkHwnd = IntPtr.Zero;
        private bool _showOcrOverlay;

        public MainWindow()
        {
            InitializeComponent();
            SettingsFile = Path.Combine(SettingsDir, "global.json");
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettingsIntoUi();
            StartArkPolling();
            SetStatus("Ready.");
            // make sure first tab is capture
            TopTabs.SelectedIndex = 0;
            CapturePage.Visibility = Visibility.Visible;
            SettingsPage.Visibility = Visibility.Collapsed;
        }

        private void Window_Closing(object? sender, CancelEventArgs e) => TrySaveFromUi();

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

        // ---------- settings ----------
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

            // load persisted overlay flag from the same global.json
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var node = JsonNode.Parse(File.ReadAllText(SettingsFile)) as JsonObject;
                    _showOcrOverlay = (bool?)node?["ShowOcrOverlay"] ?? false;
                }
            }
            catch
            {
                _showOcrOverlay = false;
            }

            ShowOcrDetailsCheck.IsChecked = _showOcrOverlay;
            OcrDetailsOverlay.Visibility = _showOcrOverlay ? Visibility.Visible : Visibility.Collapsed;
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

                var root = JsonSerializer.SerializeToNode(_settings, _json) as JsonObject ?? new JsonObject();
                root["ShowOcrOverlay"] = ShowOcrDetailsCheck.IsChecked == true;

                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsFile, root.ToJsonString(_json));

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

        // ---------- ARK window polling ----------
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

        // ---------- Buttons ----------
        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedScreenRect == null)
            {
                SetStatus("Select log area first.");
                return;
            }

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
                _selectedScreenRect = overlay.SelectedArea;
                if (_selectedScreenRect != null)
                {
                    _settings.UseCrop = true;
                    _settings.CropX = _selectedScreenRect.Value.X;
                    _settings.CropY = _selectedScreenRect.Value.Y;
                    _settings.CropW = _selectedScreenRect.Value.Width;
                    _settings.CropH = _selectedScreenRect.Value.Height;
                    TrySaveFromUi();
                }
            }
        }

        private async void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedScreenRect == null)
            {
                SetStatus("Select log area first.");
                return;
            }

            var bmp = ScreenGrabber.Grab(_selectedScreenRect.Value);
            LivePreview.Source = ToBitmapSource(bmp);

            var ocr = new OcrClient(_settings);
            var text = await ocr.OcrAsync(bmp);
            Clipboard.SetText(text ?? string.Empty);
            SetStatus("OCR text copied to clipboard.");
        }

        private async void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedScreenRect == null)
            {
                SetStatus("Select log area first.");
                return;
            }

            var bmp = ScreenGrabber.Grab(_selectedScreenRect.Value);
            LivePreview.Source = ToBitmapSource(bmp);

            var client = new GravityClient(_settings);
            await client.SendScreenshotAsync(bmp);
            await client.OcrAndPostAsync(bmp);

            SetStatus("OCR & post done.");
        }

        private void ApiEchoText_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApiEchoText.ScrollToEnd();
        }

        private void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            var text = LogLineBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("No text to send.");
                return;
            }

            var client = new GravityClient(_settings);
            _ = client.SendLogLinesAsync(text);
            SetStatus("Sent pasted logs.");
        }

        private void SaveDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfd = new SaveFileDialog
                {
                    Filter = "ZIP files (*.zip)|*.zip",
                    FileName = "gravity-capture-debug.zip"
                };
                if (sfd.ShowDialog() == true)
                {
                    using var fs = File.Open(sfd.FileName, FileMode.Create);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

                    // settings
                    if (File.Exists(SettingsFile))
                        zip.CreateEntryFromFile(SettingsFile, "global.json");

                    // window state
                    var appData = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GravityCapture");
                    var winState = Path.Combine(appData, "window.json");
                    if (File.Exists(winState))
                        zip.CreateEntryFromFile(winState, "window.json");

                    SetStatus("Debug ZIP saved.");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Debug ZIP failed: {ex.Message}");
            }
        }

        private void ShowOcrDetailsCheck_Changed(object sender, RoutedEventArgs e)
        {
            _showOcrOverlay = ShowOcrDetailsCheck.IsChecked == true;
            OcrDetailsOverlay.Visibility = _showOcrOverlay ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ServerBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // optional live save
        }

        private void TribeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // optional live save
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TopTabs.SelectedIndex == 0)
            {
                CapturePage.Visibility = Visibility.Visible;
                SettingsPage.Visibility = Visibility.Collapsed;
            }
            else
            {
                CapturePage.Visibility = Visibility.Collapsed;
                SettingsPage.Visibility = Visibility.Visible;
            }
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private void OnPreviewTick(object? sender, EventArgs e)
        {
            if (_selectedScreenRect == null) return;

            var bmp = ScreenGrabber.Grab(_selectedScreenRect.Value);
            LivePreview.Source = ToBitmapSource(bmp);

            if (_showOcrOverlay)
            {
                var ocr = new OcrClient(_settings);
                _ = ocr.OcrAsync(bmp).ContinueWith(t =>
                {
                    var txt = t.Result ?? string.Empty;
                    Dispatcher.Invoke(() => OcrDetailsText.Text = txt);
                });
            }
        }

        private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var src = new BitmapImage();
            src.BeginInit();
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.StreamSource = ms;
            src.EndInit();
            src.Freeze();
            return src;
        }

        // p/invoke ---------------------------------
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private static string GetWindowText(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
