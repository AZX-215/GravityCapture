using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        private static bool ToneBoostEnabled =>
            string.Equals(Environment.GetEnvironmentVariable("GC_CAPTURE_TONEBOOST"), "1", StringComparison.OrdinalIgnoreCase);

        public static Bitmap Capture(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
            if (!TryGetClientRectOnScreen(hwnd, out var r)) r = GetPrimaryMonitorRect();
            return CaptureScreenRect(r.left, r.top, r.right - r.left, r.bottom - r.top);
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double x, double y, double w, double h)
        {
            if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
            if (!TryGetClientRectOnScreen(hwnd, out var r)) r = GetPrimaryMonitorRect();

            int rw = r.right - r.left, rh = r.bottom - r.top;
            int cx = r.left + (int)Math.Round(x * rw);
            int cy = r.top  + (int)Math.Round(y * rh);
            int cw =          (int)Math.Round(w * rw);
            int ch =          (int)Math.Round(h * rh);

            return CaptureScreenRect(cx, cy, cw, ch);
        }

        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            if (ToneBoostEnabled) ToneBoostInPlace(bmp);
            using var ms = new MemoryStream();
            var enc = GetImageCodec(ImageFormat.Jpeg);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(quality, 1, 100));
            bmp.Save(ms, enc, ep);
            return ms.ToArray();
        }

        public static (bool ok, Rectangle rectScreen, IntPtr lastHwnd) SelectRegion(IntPtr lastKnownHwnd)
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (false, Rectangle.Empty, lastKnownHwnd);
            if (!TryGetClientRectOnScreen(hwnd, out var r)) return (false, Rectangle.Empty, lastKnownHwnd);
            return (true, new Rectangle(r.left, r.top, r.right - r.left, r.bottom - r.top), hwnd);
        }

        public static bool TryNormalizeRect(IntPtr hwnd, Rectangle rectScreen, out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (!TryGetClientRectOnScreen(hwnd, out var r)) return false;
            double rw = r.right - r.left, rh = r.bottom - r.top;
            if (rw <= 0 || rh <= 0) return false;

            double x = rectScreen.Left - r.left, y = rectScreen.Top - r.top;
            nx = Math.Clamp(x / rw, 0, 1); ny = Math.Clamp(y / rh, 0, 1);
            nw = Math.Clamp(rectScreen.Width / rw, 0, 1);
            nh = Math.Clamp(rectScreen.Height / rh, 0, 1);
            return true;
        }

        public static IntPtr ResolveWindowByTitleHint(string hint, IntPtr fallback, out IntPtr chosen)
        {
            chosen = IntPtr.Zero;
            if (string.IsNullOrWhiteSpace(hint))
            {
                chosen = fallback != IntPtr.Zero ? fallback : GetForegroundWindow();
                return chosen;
            }

            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new System.Text.StringBuilder(512);
                GetWindowText(h, sb, sb.Capacity);
                if (sb.ToString().IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) { chosen = h; return false; }
                return true;
            }, IntPtr.Zero);

            if (chosen == IntPtr.Zero) chosen = fallback != IntPtr.Zero ? fallback : GetForegroundWindow();
            return chosen;
        }

        private static Bitmap CaptureScreenRect(int x, int y, int w, int h)
        {
            var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        private static void ToneBoostInPlace(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    byte* p0 = (byte*)data.Scan0.ToPointer();
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        byte* row = p0 + y * data.Stride;
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            int o = x * 3;
                            row[o + 0] = Gamma(row[o + 0]);
                            row[o + 1] = Gamma(row[o + 1]);
                            row[o + 2] = Gamma(row[o + 2]);
                        }
                    }
                }
                static byte Gamma(byte v)
                {
                    double f = v / 255.0;
                    f = Math.Pow(f, 1.0 / 1.1);
                    return (byte)Math.Clamp((int)Math.Round(f * 255.0), 0, 255);
                }
            }
            finally { bmp.UnlockBits(data); }
        }

        private static ImageCodecInfo GetImageCodec(ImageFormat fmt)
        {
            foreach (var e in ImageCodecInfo.GetImageEncoders())
                if (e.FormatID == fmt.Guid) return e;
            return ImageCodecInfo.GetImageEncoders()[0];
        }

        private static RECT GetPrimaryMonitorRect()
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            return new RECT(b.Left, b.Top, b.Right, b.Bottom);
        }

        private static bool TryGetClientRectOnScreen(IntPtr hwnd, out RECT rectScreen)
        {
            rectScreen = default;
            if (!IsWindow(hwnd)) return false;
            if (!GetClientRect(hwnd, out var rc)) return false;
            var tl = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref tl)) return false;
            rectScreen = new RECT(tl.X, tl.Y, tl.X + rc.right, tl.Y + rc.bottom);
            return true;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool   IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool   IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int    GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool   EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; public RECT(int l, int t, int r, int b){ left=l; top=t; right=r; bottom=b; } }
    }
}
