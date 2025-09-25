// src/GravityCapture/MainWindow.xaml.cs
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using GravityCapture.Models;
using GravityCapture.Services;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings = null!;
        private RemoteOcrService? _remote;
        private DispatcherTimer? _liveTimer;
        private IntPtr _lastHwnd = IntPtr.Zero;

        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        public MainWindow()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            Activated += (_, __) => UpdatePreviewRunState();
            Deactivated += (_, __) => UpdatePreviewRunState();
            IsVisibleChanged += (_, __) => UpdatePreviewRunState();
            StateChanged += (_, __) => UpdatePreviewRunState();

            SourceInitialized += (_, __) => ApplyDarkTitleBar();
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            _settings = AppSettings.Load();
            BindFromSettings();
            LogIngestClient.Configure(_settings);
            _remote = new RemoteOcrService(_settings);

            _liveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _liveTimer.Tick += async (_, __) => await UpdateLivePreviewAsync();

            UpdatePreviewRunState(); // start or pause based on current window state
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            _liveTimer?.Stop();
            _liveTimer = null;
            LivePreview.Source = null;
        }

        private void UpdatePreviewRunState()
        {
            bool shouldRun = IsActive && IsVisible && WindowState != WindowState.Minimized;
            if (_liveTimer == null) return;

            if (shouldRun && !_liveTimer.IsEnabled)
                _liveTimer.Start();
            else if (!shouldRun && _liveTimer.IsEnabled)
            {
                _liveTimer.Stop();
                LivePreview.Source = null; // free GPU/GDI
            }
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
                const int DWMWA_USE_IMMERSIVE_DARK_MODE_NEW = 20;
                [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
                var hwnd = new WindowInteropHelper(this).Handle;
                int v = 1;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_NEW, ref v, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref v, sizeof(int));
            }
            catch { }
        }

        // ----------------- UI <-> Settings -----------------

        private void BindFromSettings()
        {
            ApiUrlBox.Text   = _settings.ApiBaseUrl ?? "";
            ApiKeyBox.Text   = _settings.Auth?.ApiKey ?? "";
            ChannelBox.Text  = _settings.Image?.ChannelId ?? "";
            IntervalBox.Text = _settings.IntervalMinutes.ToString();
            ActiveWindowCheck.IsChecked = _settings.Capture?.ActiveWindow ?? true;
            QualitySlider.Value = _settings.Image?.JpegQuality ?? 90;
            QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            ServerBox.Text = _settings.Capture?.ServerName ?? "";
            TribeBox.Text  = _settings.TribeName ?? "";
            AutoOcrCheck.IsChecked = _settings.AutoOcrEnabled;
            RedOnlyCheck.IsChecked = _settings.PostOnlyCritical;
            FilterTameCheck.IsChecked = _settings.Image?.FilterTameDeath ?? false;
            FilterStructCheck.IsChecked = _settings.Image?.FilterStructureDestroyed ?? false;
            FilterTribeCheck.IsChecked = _settings.Image?.FilterTribeMateDeath ?? false;
        }

        private void BindToSettings()
        {
            _settings.ApiBaseUrl = (ApiUrlBox.Text ?? "").Trim();
            _settings.Auth ??= new AppSettings.AuthSettings();
            _settings.Auth.ApiKey = (ApiKeyBox.Text ?? "").Trim();

            _settings.Image ??= new AppSettings.ImageSettings();
            _settings.Image.ChannelId = (ChannelBox.Text ?? "").Trim();
            _settings.Image.JpegQuality = Math.Clamp((int)QualitySlider.Value, 50, 100);

            _settings.Capture ??= new AppSettings.CaptureSettings();
            _settings.Capture.ActiveWindow = ActiveWindowCheck.IsChecked == true;
            _settings.Capture.ServerName = (ServerBox.Text ?? "").Trim();

            _settings.TribeName = (TribeBox.Text ?? "").Trim();

            _settings.IntervalMinutes = int.TryParse(IntervalBox.Text, out var m) ? Math.Max(1, m) : 1;
            _settings.AutoOcrEnabled  = AutoOcrCheck.IsChecked == true;
            _settings.PostOnlyCritical = RedOnlyCheck.IsChecked == true;

            _settings.Image.FilterTameDeath = FilterTameCheck.IsChecked == true;
            _settings.Image.FilterStructureDestroyed = FilterStructCheck.IsChecked == true;
            _settings.Image.FilterTribeMateDeath = FilterTribeCheck.IsChecked == true;
        }

        // ----------------- Buttons -----------------

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            BindToSettings();
            _settings.Save();
            LogIngestClient.Configure(_settings);
            _remote = new RemoteOcrService(_settings);
            SetStatus("Saved.");
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            UpdatePreviewRunState();
            SetStatus("Live preview running.");
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _liveTimer?.Stop();
            LivePreview.Source = null;
            SetStatus("Live preview stopped.");
        }

        private async void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            // Resolve target window by title hint
            var hint = _settings.Image?.TargetWindowHint ?? "ARK";
            _lastHwnd = ScreenCapture.ResolveWindowByTitleHint(hint, _lastHwnd, out var hwnd);
            _lastHwnd = hwnd;

            // Bounded selector when the window is known; else full screen
            (bool ok, System.Drawing.Rectangle rectScreen, IntPtr usedHwnd) = ScreenCapture.SelectRegion(_lastHwnd);
            if (!ok) { SetStatus("Selection cancelled."); return; }

            // Normalize against the game window if possible, else desktop
            if (ScreenCapture.TryNormalizeRect(usedHwnd, rectScreen, out var nx, out var ny, out var nw, out var nh) ||
                ScreenCapture.TryNormalizeRectDesktop(rectScreen, out nx, out ny, out nw, out nh))
            {
                _settings.UseCrop = true;
                _settings.CropX = nx; _settings.CropY = ny; _settings.CropW = nw; _settings.CropH = nh;
                _settings.Save();
                _lastHwnd = usedHwnd;
                SetStatus($"Region set nx={nx:F3} ny={ny:F3} w={nw:F3} h={nh:F3}");

                await UpdateLivePreviewAsync(forceCrop: true); // show cropped preview immediately
            }
            else
            {
                SetStatus("Failed to normalize selection.");
            }
        }

        private async void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_remote is null) { SetStatus("Remote OCR not configured."); return; }

            using var bmp = await CaptureForOcrAsync();
            var jpeg = ScreenCapture.ToJpegBytes(bmp, _settings.Image?.JpegQuality ?? 90);
            using var ms = new MemoryStream(jpeg);

            try
            {
                var resp = await _remote.ExtractAsync(ms, System.Threading.CancellationToken.None);
                LogLineBox.Text = string.Join(Environment.NewLine, resp.Lines ?? []);
                SetStatus($"OCR ok. {resp.Lines?.Count ?? 0} lines.");
            }
            catch (Exception ex)
            {
                SetStatus("OCR error: " + ex.Message);
            }
        }

        private void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                using var bmp = await CaptureForOcrAsync();
                var bytes = ScreenCapture.ToJpegBytes(bmp, _settings.Image?.JpegQuality ?? 90);
                Dispatcher.Invoke(() => SetStatus($"Cropped JPEG {bytes.Length:N0} bytes."));
            });
        }

        private void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Parsed log line send stub.");
        }

        // ----------------- Live preview & capture -----------------

        private async Task UpdateLivePreviewAsync(bool forceCrop = false)
        {
            try
            {
                using var bmp = await Task.Run(() =>
                {
                    var hint = _settings.Image?.TargetWindowHint ?? "ARK";
                    _lastHwnd = ScreenCapture.ResolveWindowByTitleHint(hint, _lastHwnd, out var resolved);
                    _lastHwnd = resolved;

                    bool crop = forceCrop || _settings.UseCrop;
                    if (crop && _settings.CropW > 0 && _settings.CropH > 0)
                    {
                        return ScreenCapture.CaptureCropNormalized(
                            _lastHwnd,
                            _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);
                    }
                    return ScreenCapture.Capture(_lastHwnd);
                });

                IntPtr hBmp = bmp.GetHbitmap();
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                        hBmp, IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    LivePreview.Source = src; // shows cropped region if UseCrop = true
                }
                finally { DeleteObject(hBmp); }
            }
            catch (Exception ex)
            {
                SetStatus("Preview error: " + ex.Message);
            }
        }

        private Task<Bitmap> CaptureForOcrAsync()
        {
            return Task.Run(() =>
            {
                var hint = _settings.Image?.TargetWindowHint ?? "ARK";
                _lastHwnd = ScreenCapture.ResolveWindowByTitleHint(hint, _lastHwnd, out var resolved);
                _lastHwnd = resolved;

                if (_settings.UseCrop && _settings.CropW > 0 && _settings.CropH > 0)
                {
                    return ScreenCapture.CaptureCropNormalized(
                        _lastHwnd,
                        _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);
                }
                return ScreenCapture.Capture(_lastHwnd);
            });
        }

        // ----------------- Helpers -----------------

        private void SetStatus(string text) => StatusText.Text = text;
    }
}
