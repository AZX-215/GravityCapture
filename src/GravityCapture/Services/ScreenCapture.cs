using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using SWF = System.Windows.Forms;

using GravityCapture.Views;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(System.Drawing.Point p);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); }

        public static IntPtr ResolveWindowByTitleHint(string hint, IntPtr lastHwnd, out IntPtr resolved)
        {
            resolved = IntPtr.Zero;
            if (!string.IsNullOrWhiteSpace(hint))
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
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
            if (resolved == IntPtr.Zero) resolved = lastHwnd;
            return resolved;
        }

        public static Bitmap Capture(IntPtr hwnd)
        {
            Rectangle rect;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) rect = SWF.Screen.PrimaryScreen.Bounds;
            else rect = r.ToRectangle();

            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                new System.Drawing.Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double x, double y, double w, double h)
        {
            if (w <= 0 || h <= 0) return Capture(hwnd);
            Rectangle baseRect;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) baseRect = SWF.Screen.PrimaryScreen.Bounds;
            else baseRect = r.ToRectangle();

            int rx = baseRect.Left + (int)Math.Round(baseRect.Width  * x);
            int ry = baseRect.Top  + (int)Math.Round(baseRect.Height * y);
            int rw = Math.Max(1, (int)Math.Round(baseRect.Width  * w));
            int rh = Math.Max(1, (int)Math.Round(baseRect.Height * h));

            var bmp = new Bitmap(rw, rh, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rx, ry, 0, 0, new System.Drawing.Size(rw, rh), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            using var ms = new System.IO.MemoryStream();
            var enc = GetEncoder(ImageFormat.Jpeg);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,
                Math.Min(100L, Math.Max(50L, (long)quality)));
            bmp.Save(ms, enc, ep);
            return ms.ToArray();
        }

        private static ImageCodecInfo GetEncoder(ImageFormat fmt)
        {
            foreach (var c in ImageCodecInfo.GetImageDecoders())
                if (c.FormatID == fmt.Guid) return c;
            return ImageCodecInfo.GetImageDecoders()[0];
        }

        // ---- Region selection ----

        public static (bool ok, Rectangle rectScreen, IntPtr hwndUsed) SelectRegion(IntPtr preferredHwnd)
        {
            var win = new RegionSelectorWindow();
            win.Owner = GetActiveWpfWindow();
            var ok = win.ShowDialog() == true;

            // SelectedRect is in screen coordinates (WPF device pixels)
            var r = win.SelectedRect;
            var got = ok && r.Width >= 2 && r.Height >= 2;
            var rect = got
                ? Rectangle.FromLTRB((int)r.Left, (int)r.Top, (int)(r.Left + r.Width), (int)(r.Top + r.Height))
                : Rectangle.Empty;

            IntPtr used = preferredHwnd;
            if (used == IntPtr.Zero && got)
            {
                var center = new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                used = WindowFromPoint(center);
            }

            return (got, rect, used);
        }

        private static Window GetActiveWpfWindow()
        {
            foreach (Window w in System.Windows.Application.Current.Windows)
                if (w.IsActive) return w;
            return System.Windows.Application.Current.MainWindow;
        }

        public static bool TryNormalizeRect(IntPtr hwnd, Rectangle rectScreen, out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
            var baseRect = r.ToRectangle();
            return NormalizeAgainst(baseRect, rectScreen, out nx, out ny, out nw, out nh);
        }

        public static bool TryNormalizeRectDesktop(Rectangle rectScreen, out double nx, out double ny, out double nw, out double nh)
        {
            var baseRect = SWF.Screen.PrimaryScreen.Bounds;
            return NormalizeAgainst(baseRect, rectScreen, out nx, out ny, out nw, out nh);
        }

        private static bool NormalizeAgainst(Rectangle baseRect, Rectangle rectScreen,
                                             out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (baseRect.Width <= 1 || baseRect.Height <= 1) return false;

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
