using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

using GravityCapture.Models;
using GravityCapture.Services;

using WpfCursors = System.Windows.Input.Cursors;
using WpfImage   = System.Windows.Controls.Image;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_NEW = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("gdi32.dll")]  private static extern bool DeleteObject(IntPtr hObject);

        private readonly AppSettings _settings;
        private readonly System.Timers.Timer _timer;
        private readonly OcrIngestor _ingestor = new();
        private ApiClient? _api;
        private IntPtr _lastCropHwnd = IntPtr.Zero;

        // live preview
        private readonly System.Windows.Threading.DispatcherTimer _previewTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private WpfImage? _embeddedPreviewImage;
        private Window? _previewPopupWindow;
        private WpfImage? _popupImage;

        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, __) => ApplyDarkTitleBar();

            _settings = AppSettings.Load();

            // hydrate UI
            ApiUrlBox.Text    = _settings.ApiBaseUrl ?? "";
            ApiKeyBox.Text    = _settings.Auth?.ApiKey ?? "";
            ChannelBox.Text   = _settings.Image?.ChannelId ?? "";
            IntervalBox.Text  = _settings.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
            ActiveWindowCheck.IsChecked = _settings.Capture?.ActiveWindow ?? true;
            QualitySlider.Value = _settings.Image?.JpegQuality ?? 90;
            QualityLabel.Text   = ((int)QualitySlider.Value).ToString();
            TitleHintBox.Text   = _settings.Image?.TargetWindowHint ?? string.Empty;
            ServerBox.Text      = _settings.Capture?.ServerName ?? string.Empty;
            TribeBox.Text       = _settings.TribeName ?? string.Empty;

            AutoOcrCheck.IsChecked   = _settings.AutoOcrEnabled;
            RedOnlyCheck.IsChecked   = _settings.PostOnlyCritical;
            FilterTameCheck.IsChecked   = _settings.Image?.FilterTameDeath          ?? false;
            FilterStructCheck.IsChecked = _settings.Image?.FilterStructureDestroyed ?? false;
            FilterTribeCheck.IsChecked  = _settings.Image?.FilterTribeMateDeath     ?? false;

            _timer = new System.Timers.Timer { AutoReset = true, Enabled = false };
            _timer.Elapsed += OnTick;

            Closing += (_, __) =>
            {
                try { SaveSettings(); } catch { }
                try { _previewTimer.Stop(); _previewPopupWindow?.Close(); } catch { }
            };

            // live preview bootstrap
            _embeddedPreviewImage = FindName("LivePreview") as WpfImage;
            if (_embeddedPreviewImage == null)
            {
                _popupImage = new WpfImage { Stretch = System.Windows.Media.Stretch.Uniform };
                _previewPopupWindow = new Window
                {
                    Title = "Live Preview",
                    Width = 420,
                    Height = 260,
                    Content = _popupImage,
                    Owner = this,
                    Topmost = true,
                    ResizeMode = ResizeMode.CanResizeWithGrip,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                _previewPopupWindow.Left = Left + 20;
                _previewPopupWindow.Top  = Top + Height - _previewPopupWindow.Height - 60;
                _previewPopupWindow.Show();
            }
            _previewTimer.Tick += (_, __) => UpdateLivePreview();
            _previewTimer.Start();
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int v = 1;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_NEW, ref v, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref v, sizeof(int));
            }
            catch { }
        }

        private void Status(string msg) => Dispatcher.Invoke(() => StatusText.Text = msg);

        private void SaveSettings()
        {
            _settings.ApiBaseUrl = ApiUrlBox.Text?.TrimEnd('/') ?? "";
            _settings.Auth ??= new AppSettings.AuthSettings();
            _settings.Auth.ApiKey = ApiKeyBox.Text ?? "";

            _settings.Image ??= new AppSettings.ImageSettings();
            _settings.Capture ??= new AppSettings.CaptureSettings();

            _settings.Image.ChannelId = ChannelBox.Text?.Trim() ?? "";
            _settings.IntervalMinutes = int.TryParse(IntervalBox.Text, out var m) ? Math.Max(1, m) : 1;
            _settings.Capture.ActiveWindow = ActiveWindowCheck.IsChecked == true;
            _settings.Image.JpegQuality = (int)QualitySlider.Value;
            _settings.Image.TargetWindowHint = TitleHintBox.Text ?? "";

            _settings.Capture.ServerName = ServerBox.Text?.Trim() ?? "";
            _settings.TribeName          = TribeBox.Text?.Trim() ?? "";

            _settings.AutoOcrEnabled           = AutoOcrCheck.IsChecked == true;
            _settings.PostOnlyCritical         = RedOnlyCheck.IsChecked == true;
            _settings.Image.FilterTameDeath          = FilterTameCheck.IsChecked   == true;
            _settings.Image.FilterStructureDestroyed = FilterStructCheck.IsChecked == true;
            _settings.Image.FilterTribeMateDeath     = FilterTribeCheck.IsChecked  == true;

            _settings.Save();
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            _api = new ApiClient(_settings.ApiBaseUrl ?? "", _settings.Auth?.ApiKey ?? "");
            _timer.Interval = TimeSpan.FromMinutes(_settings.IntervalMinutes).TotalMilliseconds;
            _timer.Start();
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled  = true;
            Status($"Running – every {_settings.IntervalMinutes} min.");
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled  = false;
            Status("Stopped.");
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            Status("Saved.");
        }

        private async void OnTick(object? s, ElapsedEventArgs e)
        {
            try
            {
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
                        hwnd, _settings, server, tribe,
                        async msg => await Dispatcher.InvokeAsync(() => Status(msg)));
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => Status($"Timer error: {ex.Message}"));
            }
        }

        // --- live preview ---
        private void UpdateLivePreview()
        {
            try
            {
                var hwnd = ResolveTargetWindow();
                using Bitmap bmp = _settings.UseCrop
                    ? ScreenCapture.CaptureCropNormalized(hwnd, _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH)
                    : ScreenCapture.Capture(hwnd);

                IntPtr hBmp = bmp.GetHbitmap();
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                        hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();

                    Dispatcher.Invoke(() =>
                    {
                        var target = _embeddedPreviewImage ?? _popupImage;
                        if (target != null) target.Source = src;
                    });
                }
                finally { DeleteObject(hBmp); }
            }
            catch { }
        }
        // ---------------------

        private async System.Threading.Tasks.Task OcrAndPostOnceAsync()
        {
            var hwnd = ResolveTargetWindow();
            await _ingestor.ScanAndPostAsync(
                hwnd, _settings,
                ServerBox.Text?.Trim() ?? "",
                TribeBox.Text?.Trim()  ?? "",
                async msg => await Dispatcher.InvokeAsync(() => Status(msg)));
        }

        private async System.Threading.Tasks.Task OcrCropPasteOnceAsync()
        {
            try
            {
                var hwnd = ResolveTargetWindow();
                using var bmp = ScreenCapture.CaptureCropNormalized(hwnd, _settings.CropX, _settings.CropY, _settings.CropW, _settings.CropH);
                await using var ms = new System.IO.MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;

                var remote = new RemoteOcrService(_settings);
                var res = await remote.ExtractAsync(ms, System.Threading.CancellationToken.None);
                var text = string.Join(Environment.NewLine, res.Lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                System.Windows.Clipboard.SetText(text);
                Status($"OCR to clipboard: {res.Lines.Count} lines.");
            }
            catch (Exception ex) { Status($"OCR paste error: {ex.Message}"); }
        }

        private IntPtr ResolveTargetWindow()
        {
            var hint = (TitleHintBox.Text ?? "").Trim();
            return ScreenCapture.ResolveWindowByTitleHint(hint, _lastCropHwnd, out _lastCropHwnd);
        }

        // Buttons

        private void OcrCropBtn_Click(object sender, RoutedEventArgs e) => _ = OcrCropPasteOnceAsync();

        private async void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e) => await OcrAndPostOnceAsync();

        private void SendParsedBtn_Click(object sender, RoutedEventArgs e) => _ = OcrAndPostOnceAsync();

        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Cursor = WpfCursors.Cross;
                var hwnd = ResolveTargetWindow();
                var (ok, rectScreen, lastHwnd) = ScreenCapture.SelectRegion(hwnd);
                _lastCropHwnd = lastHwnd;
                Cursor = WpfCursors.Arrow;

                if (!ok)
                {
                    Status("Region selection cancelled.");
                    return;
                }

                // try normalize against hwnd; if that fails, normalize against desktop
                if (ScreenCapture.TryNormalizeRect(lastHwnd, rectScreen, out var nx, out var ny, out var nw, out var nh) ||
                    ScreenCapture.TryNormalizeRectDesktop(rectScreen, out nx, out ny, out nw, out nh))
                {
                    _settings.UseCrop = true;
                    _settings.CropX = nx; _settings.CropY = ny; _settings.CropW = nw; _settings.CropH = nh;
                    _settings.Save();
                    Status($"Region set: x={nx:0.###} y={ny:0.###} w={nw:0.###} h={nh:0.###}");
                }
                else
                {
                    Status("Could not compute normalized crop.");
                }
            }
            catch (Exception ex)
            {
                Cursor = WpfCursors.Arrow;
                Status($"Region selection error: {ex.Message}");
            }
        }
    }
}
