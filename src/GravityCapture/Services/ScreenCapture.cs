using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using GravityCapture.Views;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); }

        // Resolve by title hint (best-effort). Returns IntPtr.Zero if not found.
        public static IntPtr ResolveWindowByTitleHint(string hint, IntPtr lastHwnd, out IntPtr resolved)
        {
            resolved = IntPtr.Zero;
            if (!string.IsNullOrWhiteSpace(hint))
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(p.MainWindowTitle) &&
                            p.MainWindowTitle.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            resolved = p.MainWindowHandle;
                            break;
                        }
                    } catch { }
                }
            }
            if (resolved == IntPtr.Zero) resolved = lastHwnd; // fall back to last
            return resolved;
        }

        public static Bitmap Capture(IntPtr hwnd)
        {
            Rectangle rect;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) rect = Screen.PrimaryScreen.Bounds;
            else rect = r.ToRectangle();

            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double x, double y, double w, double h)
        {
            if (w <= 0 || h <= 0) return Capture(hwnd);
            Rectangle baseRect;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) baseRect = Screen.PrimaryScreen.Bounds;
            else baseRect = r.ToRectangle();

            int rx = baseRect.Left + (int)Math.Round(baseRect.Width  * x);
            int ry = baseRect.Top  + (int)Math.Round(baseRect.Height * y);
            int rw = Math.Max(1, (int)Math.Round(baseRect.Width  * w));
            int rh = Math.Max(1, (int)Math.Round(baseRect.Height * h));

            var bmp = new Bitmap(rw, rh, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rx, ry, 0, 0, new Size(rw, rh), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            using var ms = new System.IO.MemoryStream();
            var enc = GetEncoder(ImageFormat.Jpeg);
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Min(100L, Math.Max(50L, (long)quality)));
            bmp.Save(ms, enc, ep);
            return ms.ToArray();
        }

        private static ImageCodecInfo GetEncoder(ImageFormat fmt)
        {
            foreach (var c in ImageCodecInfo.GetImageDecoders())
                if (c.FormatID == fmt.Guid) return c;
            return ImageCodecInfo.GetImageDecoders()[0];
        }

        // --- Region selection ---

        public static (bool ok, Rectangle rectScreen, IntPtr hwndUsed) SelectRegion(IntPtr preferredHwnd)
        {
            // Overlay allows selection anywhere on screen
            var win = new RegionSelectorWindow();
            win.Owner = GetActiveWpfWindow();
            var ok = win.ShowDialog() == true;
            Rectangle rect = Rectangle.Empty;
            if (ok && win.SelectedRect.HasValue)
            {
                var r = win.SelectedRect.Value; // WPF Rect in screen coords
                rect = Rectangle.FromLTRB((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
                if (rect.Width < 2 || rect.Height < 2) ok = false;
            }

            // Re-resolve target window after selection (mouse likely over it)
            IntPtr used = preferredHwnd;
            if (used == IntPtr.Zero)
            {
                // try the window at the center of selection
                var center = new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                used = WindowFromPoint(center);
            }

            return (ok, rect, used);
        }

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(System.Drawing.Point p);

        private static Window GetActiveWpfWindow()
        {
            foreach (Window w in Application.Current.Windows)
                if (w.IsActive) return w;
            return Application.Current.MainWindow;
        }

        // Normalize against a specific HWND; returns false if hwnd invalid
        public static bool TryNormalizeRect(IntPtr hwnd, Rectangle rectScreen, out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
            var baseRect = r.ToRectangle();
            return NormalizeAgainst(baseRect, rectScreen, out nx, out ny, out nw, out nh);
        }

        // Fallback: normalize against the desktop bounds when hwnd cannot be resolved.
        public static bool TryNormalizeRectDesktop(Rectangle rectScreen, out double nx, out double ny, out double nw, out double nh)
        {
            var baseRect = Screen.PrimaryScreen.Bounds;
            return NormalizeAgainst(baseRect, rectScreen, out nx, out ny, out nw, out nh);
        }

        private static bool NormalizeAgainst(Rectangle baseRect, Rectangle rectScreen,
                                             out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (baseRect.Width <= 1 || baseRect.Height <= 1) return false;

            // clamp to base
            var rc = Rectangle.Intersect(baseRect, rectScreen);
            if (rc.Width < 2 || rc.Height < 2) return false;

            nx = (rc.Left - baseRect.Left) / (double)baseRect.Width;
            ny = (rc.Top  - baseRect.Top ) / (double)baseRect.Height;
            nw = rc.Width  / (double)baseRect.Width;
            nh = rc.Height / (double)baseRect.Height;
            return true;
        }
    }
}

