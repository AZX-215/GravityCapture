using System;
using System.Drawing;
using System.IO;
using System.Timers;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Input;
using GravityCapture.Models;
using GravityCapture.Services;

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
                using Bitmap bmp = ScreenCapture.Capture(_settings.CaptureActiveWindow);
                var bytes = ScreenCapture.ToJpegBytes(bmp, _settings.JpegQuality);
                string fname = $"gravity_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
                bool ok = await _api.SendScreenshotAsync(bytes, fname, _settings.ChannelId, "Gravity capture");
                Status(ok ? $"Sent {fname}" : "Send failed (HTTP)");
            }
            catch (Exception ex)
            {
                Status("Error: " + ex.Message);
            }
        }

        private void Status(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

        // Stage test button
        private async void SendTestBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            WpfMouse.OverrideCursor = WpfCursors.Wait;
