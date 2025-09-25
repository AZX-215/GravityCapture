using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        // Toggle the mild post-capture contrast/gamma tweak via env var.
        // Now scoped by GC_CAPTURE_TONEBOOST only. Default OFF so HDR remains unchanged.
        private static bool ToneBoostEnabled =>
            string.Equals(Environment.GetEnvironmentVariable("GC_CAPTURE_TONEBOOST"), "1", StringComparison.OrdinalIgnoreCase);

        // -------------------------
        // Public API
        // -------------------------

        public static Bitmap Capture(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                hwnd = GetForegroundWindow();

            if (!TryGetClientRectOnScreen(hwnd, out var rect))
            {
                // Fallback to primary monitor
                rect = GetPrimaryMonitorRect();
            }

            return CaptureScreenRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double x, double y, double w, double h)
        {
            if (hwnd == IntPtr.Zero)
                hwnd = GetForegroundWindow();

            if (!TryGetClientRectOnScreen(hwnd, out var rect))
                rect = GetPrimaryMonitorRect();

            int rw = rect.right - rect.left;
            int rh = rect.bottom - rect.top;

            int cx = rect.left + (int)Math.Round(x * rw);
            int cy = rect.top  + (int)Math.Round(y * rh);
            int cw =            (int)Math.Round(w * rw);
            int ch =            (int)Math.Round(h * rh);

            return CaptureScreenRect(cx, cy, cw, ch);
        }

        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            if (ToneBoostEnabled)
                ToneBoostInPlace(bmp);

            using var ms = new MemoryStream();
            var encoder  = GetImageCodec(ImageFormat.Jpeg);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(quality, 1, 100));
            bmp.Save(ms, encoder, ep);
            return ms.ToArray();
        }

        public static (bool ok, Rectangle rectScreen, IntPtr lastHwnd) SelectRegion(IntPtr lastKnownHwnd)
        {
            // Simple rectangle selection over current foreground window.
            // For stage purposes we reuse OS selection via PrintWindow fallback.
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (false, Rectangle.Empty, lastKnownHwnd);
            if (!TryGetClientRectOnScreen(hwnd, out var r)) return (false, Rectangle.Empty, lastKnownHwnd);
            return (true, new Rectangle(r.left, r.top, r.right - r.left, r.bottom - r.top), hwnd);
        }

        public static bool TryNormalizeRect(IntPtr hwnd, Rectangle rectScreen, out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (!TryGetClientRectOnScreen(hwnd, out var r)) return false;

            double rw = r.right - r.left;
            double rh = r.bottom - r.top;
            if (rw <= 0 || rh <= 0) return false;

            double x = rectScreen.Left   - r.left;
            double y = rectScreen.Top    - r.top;
            double w = rectScreen.Width;
            double h = rectScreen.Height;

            nx = x / rw; ny = y / rh; nw = w / rw; nh = h / rh;
            nx = Math.Clamp(nx, 0, 1);
            ny = Math.Clamp(ny, 0, 1);
            nw = Math.Clamp(nw, 0, 1);
            nh = Math.Clamp(nh, 0, 1);
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

            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new System.Text.StringBuilder(512);
                GetWindowText(h, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    chosen = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (chosen == IntPtr.Zero)
                chosen = fallback != IntPtr.Zero ? fallback : GetForegroundWindow();

            return chosen;
        }

        // -------------------------
        // Internals
        // -------------------------

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
            // Mild S-curve to lift mid-tones for OCR without crushing HDR highlights.
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    byte* scan0 = (byte*)data.Scan0.ToPointer();
                    int stride = data.Stride;
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        byte* row = scan0 + y * stride;
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            byte b = row[x * 3 + 0];
                            byte g = row[x * 3 + 1];
                            byte r = row[x * 3 + 2];

                            // simple gamma-ish curve
                            row[x * 3 + 0] = Gamma(b);
                            row[x * 3 + 1] = Gamma(g);
                            row[x * 3 + 2] = Gamma(r);
                        }
                    }
                }

                static byte Gamma(byte v)
                {
                    double f = v / 255.0;
                    // 1.1 gamma
                    f = Math.Pow(f, 1.0 / 1.1);
                    return (byte)Math.Clamp((int)Math.Round(f * 255.0), 0, 255);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static ImageCodecInfo GetImageCodec(ImageFormat fmt)
        {
            var encs = ImageCodecInfo.GetImageEncoders();
            foreach (var e in encs)
                if (e.FormatID == fmt.Guid) return e;
            return encs[0];
        }

        private static RECT GetPrimaryMonitorRect()
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            return new RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
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

        // -------------------------
        // Win32
        // -------------------------
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool   IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool   IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int    GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool   EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left, top, right, bottom;
            public RECT(int l, int t, int r, int b) { left = l; top = t; right = r; bottom = b; }
        }
    }
}
