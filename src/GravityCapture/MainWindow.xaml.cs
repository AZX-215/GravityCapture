using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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

        public MainWindow()
        {
            InitializeComponent();
            QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            QualitySlider.ValueChanged += (_, __) => QualityLabel.Text = ((int)QualitySlider.Value).ToString();
            _previewTimer.Tick += (_, __) => UpdatePreview();
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
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
            Clipboard.SetImage(ToBitmapImage(bmp));
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
    }
}
