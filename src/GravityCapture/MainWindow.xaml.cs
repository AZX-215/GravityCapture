using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

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

        // Helpers for env export
        private static void SetEnvInt(string key, int val) =>
            Environment.SetEnvironmentVariable(key, val.ToString(), EnvironmentVariableTarget.Process);
        private static void SetEnvDouble(string key, double val) =>
            Environment.SetEnvironmentVariable(key, val.ToString(CultureInfo.InvariantCulture), EnvironmentVariableTarget.Process);

        public MainWindow()
        {
            InitializeComponent();

            SourceInitialized += (_, __) => ApplyDarkTitleBar();

            _settings = AppSettings.Load();

            EnvBox.SelectedIndex = _settings.LogEnvironment.Equals("Prod", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            LoadEnvFieldsIntoTextBoxes();

            // ChannelId is a string in stage config
            ChannelBox.Text  = _settings.Image?.ChannelId ?? string.Empty;
            IntervalBox.Text = _settings.IntervalMinutes.ToString();
            ActiveWindowCheck.IsChecked = _settings.Capture?.ActiveWindow ?? true;

            QualitySlider.Value = _settings.Image?.JpegQuality ?? 90;
            QualityLabel.Text   = ((int)QualitySlider.Value).ToString();
            QualitySlider.ValueChanged += (_, __) => QualityLabel.Text = ((int)QualitySlider.Value).ToString();

            TitleHintBox.Text = _settings.Image?.TargetWindowHint ?? string.Empty;
            ServerBox.Text    = _settings.Capture?.ServerName ?? string.Empty;
            TribeBox.Text     = _settings.TribeName  ?? string.Empty;

            AutoOcrCheck.IsChecked = _settings.AutoOcrEnabled;
            RedOnlyCheck.IsChecked = _settings.PostOnlyCritical;

            FilterTameCheck.IsChecked   = _settings.Image?.FilterTameDeath          ?? false;
            FilterStructCheck.IsChecked = _settings.Image?.FilterStructureDestroyed ?? false;
            FilterTribeCheck.IsChecked  = _settings.Image?.FilterTribeMateDeath     ?? false;

            LogIngestClient.Configure(_settings);

            _timer = new System.Timers.Timer { AutoReset = true, Enabled = false };
            _timer.Elapsed += OnTick;

            Closing += (_, __) => { try { SaveSettings(); } catch { } };

            // Export active OCR parameters, set label
            ApplyActiveProfile();
            UpdateActiveProfileLabel();

            ProfileManager.ProfileChanged += (_, __) =>
            {
                ApplyActiveProfile();
                UpdateActiveProfileLabel();
                Status($"OCR profile → {ProfileManager.ActiveProfile}");
            };

            // Autostart
            if (_settings.Autostart)
            {
                try { StartCapture(); } catch { }
            }
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int useImmersiveDarkMode = 1;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_NEW, ref useImmersiveDarkMode, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useImmersiveDarkMode, sizeof(int));
            }
            catch { }
        }

        private void Status(string msg) => Dispatcher.Invoke(() =>
        {
            StatusText.Text = msg;
        });

        private void ToggleOcrProfile()
        {
            var next = ProfileManager.ActiveProfile == "SDR" ? "HDR" : "SDR";
            ProfileManager.Switch(next);
            // ProfileChanged updates env + label
            WpfMessageBox.Show($"Switched OCR profile → {next}", "Gravity Capture",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ProfileToggleBtn_Click(object sender, RoutedEventArgs e) => ToggleOcrProfile();

        // --- ENVIRONMENT UI ---
        private string CurrentEnv => EnvBox.SelectedIndex == 1 ? "Prod" : "Stage";

        private void LoadEnvFieldsIntoTextBoxes()
        {
            _settings.LogEnvironment = CurrentEnv;
            ApiUrlBox.Text = _settings.ApiBaseUrl?.TrimEnd('/') ?? string.Empty;
            ApiKeyBox.Text = _settings.Auth?.ApiKey ?? string.Empty;
        }

        private void SaveTextBoxesIntoEnvFields()
        {
            _settings.LogEnvironment = CurrentEnv;
            _settings.ApiBaseUrl = ApiUrlBox.Text?.TrimEnd('/') ?? string.Empty;
            _settings.Auth ??= new AppSettings.AuthSettings();
            _settings.Auth.ApiKey = ApiKeyBox.Text ?? string.Empty;
        }

        private void EnvBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SaveTextBoxesIntoEnvFields();
            _settings.LogEnvironment = CurrentEnv;
            LoadEnvFieldsIntoTextBoxes();
            LogIngestClient.Configure(_settings);
            Status($"Switched log environment → {CurrentEnv}");
        }

        // --- SAVE / START / STOP ---
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
            SaveTextBoxesIntoEnvFields();

            _settings.Image ??= new AppSettings.ImageSettings();
            _settings.Capture ??= new AppSettings.CaptureSettings();

            _settings.Image.ChannelId   = ChannelBox.Text?.Trim() ?? string.Empty;
            _settings.IntervalMinutes   = int.TryParse(IntervalBox.Text, out var m) ? Math.Max(1, m) : 1;
            _settings.Capture.ActiveWindow = ActiveWindowCheck.IsChecked == true;
            _settings.Image.JpegQuality = (int)QualitySlider.Value;
            _settings.Image.TargetWindowHint = TitleHintBox.Text ?? string.Empty;

            _settings.Capture.ServerName = ServerBox.Text?.Trim() ?? string.Empty;
            _settings.TribeName          = TribeBox.Text?.Trim()  ?? string.Empty;

            _settings.AutoOcrEnabled           = AutoOcrCheck.IsChecked == true;
            _settings.PostOnlyCritical         = RedOnlyCheck.IsChecked == true;
            _settings.Image.FilterTameDeath          = FilterTameCheck.IsChecked   == true;
            _settings.Image.FilterStructureDestroyed = FilterStructCheck.IsChecked == true;
            _settings.Image.FilterTribeMateDeath     = FilterTribeCheck.IsChecked  == true;

            _settings.Save();
            LogIngestClient.Configure(_settings);
        }

        private void StartCapture()
        {
            // Uses ApiBaseUrl/Auth.ApiKey now
            _api = new ApiClient(_settings.ApiBaseUrl ?? "", _settings.Auth?.ApiKey ?? "");
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

        // --- TIMER TICK ---
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
                    bmp = ScreenCapture.Capture(hwnd);

                using (bmp)
                {
                    var bytes = ScreenCapture.ToJpegBytes(bmp, _settings.Image?.JpegQuality ?? 90);
                    string fname = $"gravity_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                    // ChannelId is string now
                    bool ok = await _api.SendScreenshotAsync(bytes, fname, _settings.Image?.ChannelId ?? "", "Gravity capture");
                    Status(ok ? $"Sent {fname}" : "Send failed (HTTP)");
                }
            }
            catch (Exception ex)
            {
                Status($"Capture error: {ex.Message}");
            }
        }

        private IntPtr ResolveTargetWindow()
        {
            var hint = (TitleHintBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(hint))
                hint = _settings.Image?.TargetWindowHint ?? "ARK";
            return ScreenCapture.ResolveWindowByTitleHint(hint, _lastCropHwnd, out _lastCropHwnd);
        }

        // --- region selection UI (unchanged) ---
        private void SelectRegionBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Cursor = WpfCursors.Cross;
                var hwnd = ResolveTargetWindow();
                var (success, rectScreen, lastHwnd) = RegionSelectorWindow.SelectRegion(hwnd);
                _lastCropHwnd = lastHwnd;
                Cursor = WpfCursors.Arrow;

                if (success)
                {
                    var r = rectScreen;
                    // Normalize to [0..1] relative to window/client rect
                    if (ScreenCapture.TryNormalizeRect(lastHwnd, r, out var nx, out var ny, out var nw, out var nh))
                    {
                        _settings.UseCrop = true;
                        _settings.CropX   = nx; _settings.CropY = ny; _settings.CropW = nw; _settings.CropH = nh;
                        _settings.Save();
                        Status($"Region set: x={nx:0.###} y={ny:0.###} w={nw:0.###} h={nh:0.###}");
                    }
                    else
                    {
                        Status("Could not compute normalized crop.");
                    }
                }
            }
            catch (Exception ex)
            {
                Cursor = WpfCursors.Arrow;
                Status($"Region selection error: {ex.Message}");
            }
        }

        private void CopyActiveProfileBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ProfileManager.ActiveProfile);
                Status("Profile name copied.");
            }
            catch { }
        }

        private void UpdateActiveProfileLabel()
        {
            ActiveProfileText.Text = $"Active OCR Profile: {ProfileManager.ActiveProfile}";
        }

        private void ApplyActiveProfile()
        {
            var p = ProfileManager.GetActive();
            SetEnvInt("OCR_BIN", (int)p.Engine);
            SetEnvDouble("OCR_SCALE", p.Scale);
            SetEnvDouble("OCR_BIN_THR", p.BinaryThreshold);
            SetEnvDouble("OCR_STDDEV", p.StdDev);
            SetEnvInt("OCR_ERODE", p.Erode);
            SetEnvInt("OCR_DILATE", p.Dilate);
            SetEnvInt("OCR_MIN_H", p.MinHeight);
            SetEnvInt("OCR_MAX_H", p.MaxHeight);
            SetEnvDouble("OCR_MIN_AR", p.MinAspectRatio);
            SetEnvDouble("OCR_MAX_AR", p.MaxAspectRatio);
            SetEnvDouble("OCR_MIN_SOL", p.MinSolidity);
            SetEnvDouble("OCR_MIN_CONTRAST", p.MinContrast);
            SetEnvDouble("OCR_MIN_DARK", p.MinDarkPct);
            SetEnvDouble("OCR_MAX_DARK", p.MaxDarkPct);
            SetEnvDouble("OCR_MIN_LIGHT", p.MinLightPct);
            SetEnvDouble("OCR_MAX_LIGHT", p.MaxLightPct);
        }
    }
}
