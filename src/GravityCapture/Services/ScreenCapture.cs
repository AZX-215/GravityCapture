using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using SWF = System.Windows.Forms;

using GravityCapture.Views;

// Alias the GDI+ PixelFormat to avoid clash with System.Windows.Media.PixelFormat
using DPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(System.Drawing.Point p);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

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
                    }
                    catch { /* ignore */ }
                }
            }
            if (resolved == IntPtr.Zero) resolved = lastHwnd;
            return resolved;
        }

        public static bool TryGetWindowRect(IntPtr hwnd, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetWindowRect(hwnd, out var r)) return false;
            rect = r.ToRectangle();
            return rect.Width > 1 && rect.Height > 1;
        }

        public static Bitmap Capture(IntPtr hwnd)
        {
            Rectangle rect;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
                rect = SWF.Screen.PrimaryScreen.Bounds;
            else
                rect = r.ToRectangle();

            var bmp = new Bitmap(rect.Width, rect.Height, DPixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                new System.Drawing.Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double x, double y, double w, double h)
        {
            if (w <= 0 || h <= 0) return Capture(hwnd);

            Rectangle baseRect;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
                baseRect = SWF.Screen.PrimaryScreen.Bounds;
            else
                baseRect = r.ToRectangle();

            int rx = baseRect.Left + (int)Math.Round(baseRect.Width * x);
            int ry = baseRect.Top + (int)Math.Round(baseRect.Height * y);
            int rw = Math.Max(1, (int)Math.Round(baseRect.Width * w));
            int rh = Math.Max(1, (int)Math.Round(baseRect.Height * h));

            var bmp = new Bitmap(rw, rh, DPixelFormat.Format24bppRgb);
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

        // ---------------- Region selection (bounded when we know the game window) ----------------
        public static (bool ok, Rectangle rectScreen, IntPtr hwndUsed) SelectRegion(IntPtr preferredHwnd)
        {
            RegionSelectorWindow win;
            if (TryGetWindowRect(preferredHwnd, out var wrect))
            {
                // Limit overlay to the game window (px → DIPs inside the window ctor)
                win = new RegionSelectorWindow(wrect);
            }
            else
            {
                win = new RegionSelectorWindow { WindowState = WindowState.Maximized };
            }

            win.Owner = GetActiveWpfWindow();
            var ok = win.ShowDialog() == true;

            var rDip = win.SelectedRect;                    // screen coords in DIPs
            var got = ok && rDip.Width >= 2 && rDip.Height >= 2;

            // Convert back to pixels using the overlay DPI
            var rectPx = Rectangle.Empty;
            if (got)
            {
                var dpi = VisualTreeHelper.GetDpi(win);
                int left   = (int)Math.Round(rDip.Left * dpi.DpiScaleX);
                int top    = (int)Math.Round(rDip.Top  * dpi.DpiScaleY);
                int right  = (int)Math.Round((rDip.Left + rDip.Width)  * dpi.DpiScaleX);
                int bottom = (int)Math.Round((rDip.Top  + rDip.Height) * dpi.DpiScaleY);
                rectPx = Rectangle.FromLTRB(left, top, right, bottom);
            }

            IntPtr used = preferredHwnd;
            if (used == IntPtr.Zero && got)
            {
                var center = new System.Drawing.Point(rectPx.Left + rectPx.Width / 2,
                                                      rectPx.Top  + rectPx.Height / 2);
                used = WindowFromPoint(center);
            }

            return (got, rectPx, used);
        }

        private static Window GetActiveWpfWindow()
        {
            foreach (Window w in System.Windows.Application.Current.Windows)
                if (w.IsActive) return w;
            return System.Windows.Application.Current.MainWindow!;
        }

        public static bool TryNormalizeRect(IntPtr hwnd, Rectangle rectScreen,
                                            out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
            var baseRect = r.ToRectangle();
            return NormalizeAgainst(baseRect, rectScreen, out nx, out ny, out nw, out nh);
        }

        public static bool TryNormalizeRectDesktop(Rectangle rectScreen,
                                                   out double nx, out double ny, out double nw, out double nh)
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
