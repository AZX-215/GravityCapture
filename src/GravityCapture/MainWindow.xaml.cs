using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GravityCapture.Services;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private bool _haveCrop = false;
        private double _nx, _ny, _nw, _nh;
        private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };

        // App settings
        private AppSettings _settings = AppSettings.Load();

        public MainWindow()
        {
            InitializeComponent();

            // UI ← settings
            BindFromSettings();

            QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            QualitySlider.ValueChanged += (_, __) => QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            _previewTimer.Tick += (_, __) => UpdatePreview();
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            // UI → settings
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
            StatusText.Text = "Preview stopped.";
        }

        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            var (ok, rect, hwndUsed) = ScreenCapture.SelectRegion(_hwnd);
            if (!ok) { StatusText.Text = "Selection cancelled."; return; }

            _hwnd = hwndUsed;

            bool normOk = (_hwnd != IntPtr.Zero)
                ? ScreenCapture.TryNormalizeRect(_hwnd, rect, out _nx, out _ny, out _nw, out _nh)
                : ScreenCapture.TryNormalizeRectDesktop(rect, out _nx, out _ny, out _nw, out _nh);

            _haveCrop = normOk;
            StatusText.Text = normOk ? "Crop set." : "Failed to normalize crop.";
            UpdatePreview();
        }

        private void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_haveCrop) { StatusText.Text = "No crop set."; return; }
            using var bmp = ScreenCapture.CaptureCropNormalized(_hwnd, _nx, _ny, _nw, _nh);
            System.Windows.Clipboard.SetImage(ToBitmapImage(bmp));   // disambiguated
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

        private void UpdatePreview()
        {
            try
            {
                using Bitmap bmp = _haveCrop
                    ? ScreenCapture.CaptureCropNormalized(_hwnd, _nx, _ny, _nw, _nh)
                    : ScreenCapture.Capture(_hwnd);

                LivePreview.Source = ToBitmapImage(bmp);
            }
            catch (Exception ex)
            {
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

        // ---------- Settings binding ----------
        private void BindFromSettings()
        {
            ChannelBox.Text = _settings.ChannelId ?? "";
            ApiUrlBox.Text = _settings.ApiUrl ?? "";
            ApiKeyBox.Text = _settings.ApiKey ?? "";
            IntervalBox.Text = Math.Max(1, _settings.IntervalMinutes).ToString();
            ActiveWindowCheck.IsChecked = _settings.ActiveWindowOnly;
            ServerBox.Text = _settings.Server ?? "";
            TribeBox.Text = _settings.Tribe ?? "";
            QualitySlider.Value = _settings.JpegQuality <= 0 ? 85 : _settings.JpegQuality;
        }

        // Called by Save button and by MainWindow.Persistence.cs on shutdown
        private void BindToSettings()
        {
            _settings.ChannelId = ChannelBox.Text?.Trim() ?? "";
            _settings.ApiUrl = ApiUrlBox.Text?.Trim() ?? "";
            _settings.ApiKey = ApiKeyBox.Text?.Trim() ?? "";
            _settings.ActiveWindowOnly = ActiveWindowCheck.IsChecked == true;
            _settings.Server = ServerBox.Text?.Trim() ?? "";
            _settings.Tribe = TribeBox.Text?.Trim() ?? "";
            _settings.JpegQuality = (int)QualitySlider.Value;

            if (!int.TryParse(IntervalBox.Text, out var mins) || mins < 1) mins = 1;
            _settings.IntervalMinutes = mins;
        }
    }

    // ---------- Simple JSON settings ----------
    public sealed class AppSettings
    {
        public string ChannelId { get; set; } = "";
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int IntervalMinutes { get; set; } = 1;
        public bool ActiveWindowOnly { get; set; }
        public string Server { get; set; } = "";
        public string Tribe { get; set; } = "";
        public int JpegQuality { get; set; } = 85;

        private static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "GravityCapture", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }
}
