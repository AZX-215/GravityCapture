#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;                       // Canvas, WPF Image
using System.Windows.Media;
using System.Windows.Media.Imaging;

// --- Explicit aliases to avoid WPF/WinForms/GDI name clashes ---
using WpfImage = System.Windows.Controls.Image;
using GdiBitmap = System.Drawing.Bitmap;
using GdiRectangle = System.Drawing.Rectangle;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        private object? _settings;                 // loaded via reflection
        private IntPtr _arkHwnd = IntPtr.Zero;

        private bool _hasCrop;
        private double _nx, _ny, _nw, _nh;         // normalized crop (0..1)
        private GdiRectangle _lastRawRect;

        private readonly System.Timers.Timer _previewTimer;
        private readonly object _frameLock = new();
        private GdiBitmap? _lastPreviewBmp;

        private bool _showOcrDetails;

        public MainWindow()
        {
            InitializeComponent();

            _settings = SettingsCompat.TryLoad();

            _previewTimer = new System.Timers.Timer(1000.0 / 6.0);
            _previewTimer.Elapsed += (_, __) => TryUpdatePreview();
            _previewTimer.AutoReset = true;

            Loaded += Window_Loaded;
            Closed += Window_Closed;
        }

        private void Window_Loaded(object? sender, RoutedEventArgs e)
        {
            _arkHwnd = SC.ResolveArkWindow();
            Status(_arkHwnd == IntPtr.Zero ? "Ready — No Ark window selected." : "Ready");
            _previewTimer.Start();
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _previewTimer.Stop();
            lock (_frameLock)
            {
                _lastPreviewBmp?.Dispose();
                _lastPreviewBmp = null;
            }
        }

        // ---------- Top buttons ----------
        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BindToSettings();
                SettingsCompat.TrySave(_settings);          // explicit reflection-save
                Status("Settings saved.");
            }
            catch (Exception ex) { Status("Save failed: " + ex.Message); }
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Start();
            Status("Preview running.");
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _previewTimer.Stop();
            Status("Preview stopped.");
        }

        // ---------- Crop selection ----------
        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            EnsureArkWindow();

            var (ok, rect, hwndUsed) = SC.SelectRegion(_arkHwnd);
            if (!ok || rect.Width <= 0 || rect.Height <= 0)
            {
                Status("Selection canceled.");
                return;
            }

            if (hwndUsed != IntPtr.Zero) _arkHwnd = hwndUsed;
            _lastRawRect = rect;

            if (SC.TryNormalizeRect(_arkHwnd, rect, out _nx, out _ny, out _nw, out _nh))
            {
                _hasCrop = true;
                Status($"Crop set  nx={_nx:F3} ny={_ny:F3} nw={_nw:F3} nh={_nh:F3}");
                Dispatcher.Invoke(RedrawOverlay);
            }
            else
            {
                _hasCrop = false;
                Status("Failed to normalize selection.");
            }
        }

        // ---------- OCR helpers ----------
        private void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            EnsureArkWindow();
            if (!_hasCrop) { Status("No crop set."); return; }

            try
            {
                using GdiBitmap bmp = SC.CaptureCropNormalized(_arkHwnd, _nx, _ny, _nw, _nh);
                System.Windows.Clipboard.SetImage(ToBitmapSource(bmp));   // WPF clipboard
                Status("Cropped image copied to clipboard.");
            }
            catch (Exception ex) { Status("Crop failed: " + ex.Message); }
        }

        private void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            Status("Not implemented in this build. Use OCR Crop → Paste.");
        }

        private void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(LogLineBox.Text)) { Status("Nothing to send."); return; }
            Status("Sent pasted line (local stub).");
        }

        // ---------- Debug UI ----------
        private void ShowOcrDetailsCheck_Changed(object sender, RoutedEventArgs e)
        {
            _showOcrDetails = (ShowOcrDetailsCheck.IsChecked == true);
            RedrawOverlay();
        }

        private void SaveDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string root = Path.Combine(desktop, "GravityCapture_Debug_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string zipPath = root + ".zip";

            Directory.CreateDirectory(root);

            try
            {
                lock (_frameLock) { _lastPreviewBmp?.Save(Path.Combine(root, "preview.png")); }

                if (_hasCrop)
                {
                    File.WriteAllText(Path.Combine(root, "crop.json"),
                        $"{{\"nx\":{_nx},\"ny\":{_ny},\"nw\":{_nw},\"nh\":{_nh}}}");
                }

                var sb = new StringBuilder();
                sb.AppendLine("arkHwnd: " + _arkHwnd);
                sb.AppendLine("hasCrop: " + _hasCrop);
                sb.AppendLine("timestamp: " + DateTime.Now.ToString("O"));
                File.WriteAllText(Path.Combine(root, "meta.txt"), sb.ToString());

                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(root, zipPath);
                Directory.Delete(root, true);

                Status("Saved: " + zipPath);
            }
            catch (Exception ex) { Status("Save debug ZIP failed: " + ex.Message); }
        }

        // ---------- Preview loop ----------
        private void TryUpdatePreview()
        {
            try
            {
                EnsureArkWindow();
                if (_arkHwnd == IntPtr.Zero)
                {
                    Dispatcher.Invoke(() =>
                    {
                        LivePreview.Source = null;
                        Status("No Ark window selected.");
                        RedrawOverlay();
                    });
                    return;
                }

                bool usedFallback;
                string? reason;
                using GdiBitmap bmp = SC.CaptureForPreview(_arkHwnd, out usedFallback, out reason);

                lock (_frameLock)
                {
                    _lastPreviewBmp?.Dispose();
                    _lastPreviewBmp = (GdiBitmap)bmp.Clone();
                }

                Dispatcher.Invoke(() =>
                {
                    LivePreview.Source = ToBitmapSource(bmp);
                    if (usedFallback && !string.IsNullOrEmpty(reason))
                        Status("Preview: screen fallback (" + reason + ")");
                    else if (usedFallback)
                        Status("Preview: screen fallback");
                    else
                        Status("Preview: WGC");

                    RedrawOverlay();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    Status("Preview error: " + ex.Message);
                    LivePreview.Source = null;
                    RedrawOverlay();
                });
            }
        }

        // ---------- Overlay ----------
        private void LivePreview_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawOverlay();

        private void RedrawOverlay()
        {
            OcrOverlay.Children.Clear();
            if (!_showOcrDetails || !_hasCrop || LivePreview.Source == null) return;

            var src = (BitmapSource)LivePreview.Source;
            double iw = src.PixelWidth, ih = src.PixelHeight;
            double cw = LivePreview.ActualWidth, ch = LivePreview.ActualHeight;
            if (iw <= 0 || ih <= 0 || cw <= 0 || ch <= 0) return;

            double scale = Math.Min(cw / iw, ch / ih);
            double vw = iw * scale, vh = ih * scale;
            double ox = (cw - vw) / 2.0, oy = (ch - vh) / 2.0;

            double rx = ox + _nx * vw, ry = oy + _ny * vh, rw = _nw * vw, rh = _nh * vh;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, rw),
                Height = Math.Max(1, rh),
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(125, 127, 255)),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 125, 127, 255))
            };
            Canvas.SetLeft(rect, rx);
            Canvas.SetTop(rect, ry);
            OcrOverlay.Children.Add(rect);
        }

        // ---------- Helpers ----------
        private void EnsureArkWindow()
        {
            if (_arkHwnd == IntPtr.Zero) _arkHwnd = SC.ResolveArkWindow();
        }

        private static BitmapSource ToBitmapSource(GdiBitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        private void BindToSettings()
        {
            if (_settings is null) return;

            SettingsCompat.TrySet(_settings, "ChannelId", ChannelBox.Text?.Trim() ?? "");
            SettingsCompat.TrySet(_settings, "ApiUrl",     ApiUrlBox.Text?.Trim() ?? "");
            SettingsCompat.TrySet(_settings, "ApiKey",     ApiKeyBox.Text?.Trim() ?? "");
            SettingsCompat.TrySet(_settings, "Server",     ServerBox.Text?.Trim() ?? "");
            SettingsCompat.TrySet(_settings, "Tribe",      TribeBox.Text?.Trim() ?? "");
            SettingsCompat.TrySet(_settings, "JpegQuality",(int)QualitySlider.Value);
            SettingsCompat.TrySet(_settings, "ActiveWindowOnly", ActiveWindowCheck.IsChecked == true);
        }

        private void Status(string text) => StatusText.Text = text;

        // ================== ScreenCapture call-through ==================
        private static class SC
        {
            private static readonly Type? T =
                Type.GetType("GravityCapture.Services.ScreenCapture, GravityCapture");

            public static IntPtr ResolveArkWindow()
            {
                var m = T?.GetMethod("ResolveArkWindow", BindingFlags.Public | BindingFlags.Static);
                if (m != null) try { return (IntPtr)m.Invoke(null, null)!; } catch { }
                return IntPtr.Zero;
            }

            public static (bool ok, GdiRectangle rect, IntPtr hwndUsed) SelectRegion(IntPtr hwndHint)
            {
                var m = T?.GetMethod("SelectRegion", BindingFlags.Public | BindingFlags.Static);
                if (m != null)
                {
                    try
                    {
                        var res = m.Invoke(null, new object[] { hwndHint })!;
                        bool ok = (bool)res.GetType().GetProperty("ok")!.GetValue(res)!;
                        var rect = (GdiRectangle)res.GetType().GetProperty("rect")!.GetValue(res)!;
                        var used = (IntPtr)res.GetType().GetProperty("hwndUsed")!.GetValue(res)!;
                        return (ok, rect, used);
                    }
                    catch { }
                }
                return (false, GdiRectangle.Empty, IntPtr.Zero);
            }

            public static bool TryNormalizeRect(IntPtr hwnd, GdiRectangle r,
                out double nx, out double ny, out double nw, out double nh)
            {
                var m = T?.GetMethod("TryNormalizeRect", BindingFlags.Public | BindingFlags.Static);
                if (m != null)
                {
                    object[] args = { hwnd, r, 0d, 0d, 0d, 0d };
                    try
                    {
                        bool ok = (bool)m.Invoke(null, args)!;
                        nx = (double)args[2]; ny = (double)args[3];
                        nw = (double)args[4]; nh = (double)args[5];
                        return ok;
                    }
                    catch { }
                }

                if (hwnd != IntPtr.Zero && Win.GetWindowRect(hwnd, out var wr))
                {
                    double w = Math.Max(1, wr.Width);
                    double h = Math.Max(1, wr.Height);
                    nx = (r.Left - wr.Left) / w;
                    ny = (r.Top - wr.Top) / h;
                    nw = r.Width / w;
                    nh = r.Height / h;
                    return true;
                }
                nx = ny = nw = nh = 0;
                return false;
            }

            public static GdiBitmap CaptureCropNormalized(IntPtr hwnd, double nx, double ny, double nw, double nh)
            {
                var m = T?.GetMethod("CaptureCropNormalized", BindingFlags.Public | BindingFlags.Static);
                if (m != null) try { return (GdiBitmap)m.Invoke(null, new object[] { hwnd, nx, ny, nw, nh })!; } catch { }

                using GdiBitmap full = CaptureWindowBitmap(hwnd);
                var crop = new GdiRectangle(
                    (int)Math.Round(nx * full.Width),
                    (int)Math.Round(ny * full.Height),
                    (int)Math.Round(nw * full.Width),
                    (int)Math.Round(nh * full.Height));
                crop.Intersect(new GdiRectangle(0, 0, full.Width, full.Height));

                var bmp = new GdiBitmap(crop.Width, crop.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                g.DrawImage(full, new GdiRectangle(0, 0, bmp.Width, bmp.Height), crop, GraphicsUnit.Pixel);
                return bmp;
            }

            public static GdiBitmap CaptureForPreview(IntPtr hwnd, out bool usedFallback, out string? reason)
            {
                var m = T?.GetMethod("CaptureForPreview", BindingFlags.Public | BindingFlags.Static);
                if (m != null)
                {
                    object[] args = { hwnd, false, null! };
                    try
                    {
                        var bmp = (GdiBitmap)m.Invoke(null, args)!;
                        usedFallback = (bool)args[1];
                        reason = args[2]?.ToString();
                        return bmp;
                    }
                    catch { }
                }
                usedFallback = true;
                reason = "CreateForWindow failed";
                return CaptureWindowBitmap(hwnd);
            }

            private static GdiBitmap CaptureWindowBitmap(IntPtr hwnd)
            {
                if (hwnd == IntPtr.Zero) hwnd = Win.GetDesktopWindow();
                Win.GetWindowRect(hwnd, out var r);
                int w = Math.Max(1, r.Width), h = Math.Max(1, r.Height);

                IntPtr hSrc = Win.GetWindowDC(hwnd);
                IntPtr hMem = Win.CreateCompatibleDC(hSrc);
                IntPtr hBmp = Win.CreateCompatibleBitmap(hSrc, w, h);
                IntPtr hOld = Win.SelectObject(hMem, hBmp);

                const int SRCCOPY = 0x00CC0020;
                Win.BitBlt(hMem, 0, 0, w, h, hSrc, 0, 0, SRCCOPY);

                var bmp = System.Drawing.Image.FromHbitmap(hBmp);
                Win.SelectObject(hMem, hOld);
                Win.DeleteObject(hBmp);
                Win.DeleteDC(hMem);
                Win.ReleaseDC(hwnd, hSrc);
                return new GdiBitmap(bmp);
            }
        }

        // ---------- Win32 helpers (fallback) ----------
        private static class Win
        {
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

            [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
            [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr hWnd);
            [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
            [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
            [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
            [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
            [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
            [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
            [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
        }

        // ---------- Settings reflection ----------
        private static class SettingsCompat
        {
            private static Type? T =>
                Type.GetType("GravityCapture.Models.AppSettings, GravityCapture");

            public static object? TryLoad()
            {
                try
                {
                    var m = T?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                    return m?.Invoke(null, null);
                }
                catch { return null; }
            }

            public static void TrySave(object? settings)
            {
                if (settings == null) return;
                try
                {
                    var m = settings.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance);
                    m?.Invoke(settings, null);
                }
                catch { }
            }

            public static void TrySet(object settings, string propName, object? value)
            {
                try
                {
                    var p = settings.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.CanWrite)
                    {
                        var targetType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                        object? converted = value;
                        if (value != null && targetType != value.GetType())
                            converted = Convert.ChangeType(value, targetType);
                        p.SetValue(settings, converted);
                    }
                }
                catch { }
            }
        }
    }
}
