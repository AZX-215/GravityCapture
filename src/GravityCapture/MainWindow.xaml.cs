using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GravityCapture.Models;
using GravityCapture.Services;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private bool _haveCrop = false;
        private double _nx, _ny, _nw, _nh;
        private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };

        private AppSettings _settings = AppSettings.Load();

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        public MainWindow()
        {
            InitializeComponent();
            BindFromSettings();

            // Prefer Ark automatically
            var ark = ScreenCapture.ResolveArkWindow();
            if (ark != IntPtr.Zero) _hwnd = ark;

            QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            QualitySlider.ValueChanged += (_, __) => QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            _previewTimer.Tick += (_, __) => UpdatePreview();

            Loaded += (_, __) =>
            {
                _previewTimer.Start();
                UpdatePreview();
            };
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            BindToSettings();
            _settings.Save();
            StatusText.Text = "Saved.";
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Start();
            StatusText.Text = "Preview running.";
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Stop();
            LivePreview.Source = null;
            StatusText.Text = "Preview stopped.";
        }

        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            // Pass Ark (or last) to lock selector to that window
            if (_hwnd == IntPtr.Zero)
            {
                var ark = ScreenCapture.ResolveArkWindow();
                if (ark != IntPtr.Zero) _hwnd = ark;
            }

            var (ok, rect, hwndUsed) = ScreenCapture.SelectRegion(_hwnd);
            if (!ok) { StatusText.Text = "Selection cancelled."; return; }

            _hwnd = hwndUsed != IntPtr.Zero ? hwndUsed : _hwnd;

            bool normOk = (_hwnd != IntPtr.Zero)
                ? ScreenCapture.TryNormalizeRect(_hwnd, rect, out _nx, out _ny, out _nw, out _nh)
                : ScreenCapture.TryNormalizeRectDesktop(rect, out _nx, out _ny, out _nw, out _nh);

            _haveCrop = normOk;

            if (normOk)
            {
                _settings.UseCrop = true;
                _settings.CropX = _nx; _settings.CropY = _ny; _settings.CropW = _nw; _settings.CropH = _nh;
                _settings.Save();
            }

            StatusText.Text = normOk ? "Crop set." : "Failed to normalize crop.";
            UpdatePreview();
        }

        private void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_haveCrop) { StatusText.Text = "No crop set."; return; }
            using var bmp = ScreenCapture.CaptureCropNormalized(_hwnd, _nx, _ny, _nw, _nh);
            System.Windows.Clipboard.SetImage(ToBitmapImage(bmp));
            StatusText.Text = "Cropped image copied.";
        }

        private void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_haveCrop) { StatusText.Text = "No crop set."; return; }
            using var bmp = ScreenCapture.CaptureCropNormalized(_hwnd, _nx, _ny, _nw, _nh);
            LivePreview.Source = ToBitmapImage(bmp);
            StatusText.Text = "Captured now.";
        }

        private void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Sent.";
        }

        // Preview rules:
        //  - Require a valid window handle and not minimized.
        //  - If "Capture active window only" is checked, require foreground.
        //  - Occlusion by other apps does NOT matter (WGC ignores it).
        private bool PreviewAllowed()
        {
            if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return false;
            if (IsIconic(_hwnd)) return false;

            bool requireForeground = _settings.Capture?.ActiveWindow ?? false; // default false: allow occluded preview
            return !requireForeground || GetForegroundWindow() == _hwnd;
        }

        private void UpdatePreview()
        {
            try
            {
                if (!PreviewAllowed())
                {
                    LivePreview.Source = null;
                    return;
                }

                using Bitmap bmp = _haveCrop
                    ? ScreenCapture.CaptureCropNormalized(_hwnd, _nx, _ny, _nw, _nh)
                    : ScreenCapture.Capture(_hwnd);

                LivePreview.Source = ToBitmapImage(bmp);
            }
            catch (Exception ex)
            {
                LivePreview.Source = null;
                StatusText.Text = $"Preview error: {ex.Message}";
            }
        }

        private static BitmapImage ToBitmapImage(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }

        // settings ↔ UI
        private void BindFromSettings()
        {
            ChannelBox.Text = _settings.Image?.ChannelId ?? "";
            ApiUrlBox.Text   = _settings.ApiBaseUrl ?? "";
            ApiKeyBox.Text   = _settings.Auth?.ApiKey ?? "";
            IntervalBox.Text = Math.max(1, _settings.IntervalMinutes).ToString(); // if Math.max not exists: replace with Math.Max

            ActiveWindowCheck.IsChecked = _settings.Capture?.ActiveWindow ?? false; // default false to allow occluded
            ServerBox.Text = _settings.Capture?.ServerName ?? "";
            TribeBox.Text  = _settings.TribeName ?? "";

            QualitySlider.Value     = _settings.Image?.JpegQuality ?? 90;
            AutoOcrCheck.IsChecked  = _settings.AutoOcrEnabled;
            RedOnlyCheck.IsChecked  = _settings.PostOnlyCritical;

            FilterTameCheck.IsChecked    = _settings.Image?.FilterTameDeath ?? false;
            FilterStructCheck.IsChecked  = _settings.Image?.FilterStructureDestroyed ?? false;
            FilterTribeCheck.IsChecked   = _settings.Image?.FilterTribeMateDeath ?? false;
        }

        private void BindToSettings()
        {
            _settings.Image   ??= new AppSettings.ImageSettings();
            _settings.Capture ??= new AppSettings.CaptureSettings();
            _settings.Auth    ??= new AppSettings.AuthSettings();

            _settings.Image.ChannelId = ChannelBox.Text?.Trim() ?? "";
            _settings.ApiBaseUrl      = ApiUrlBox.Text?.Trim() ?? "";
            _settings.Auth.ApiKey     = ApiKeyBox.Text?.Trim() ?? "";
            _settings.TribeName       = TribeBox.Text?.Trim() ?? "";

            if (!int.TryParse(IntervalBox.Text, out var mins) || mins < 1) mins = 1;
            _settings.IntervalMinutes   = mins;
            _settings.Capture.ActiveWindow = ActiveWindowCheck.IsChecked == true;
            _settings.Capture.ServerName   = ServerBox.Text?.Trim() ?? "";

            _settings.Image.JpegQuality = (int)QualitySlider.Value;
            _settings.AutoOcrEnabled    = AutoOcrCheck.IsChecked == true;
            _settings.PostOnlyCritical  = RedOnlyCheck.IsChecked == true;

            _settings.Image.FilterTameDeath          = FilterTameCheck.IsChecked == true;
            _settings.Image.FilterStructureDestroyed = FilterStructCheck.IsChecked == true;
            _settings.Image.FilterTribeMateDeath     = FilterTribeCheck.IsChecked == true;
        }
    }
}
