using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        // Toggle the mild post-capture contrast/gamma tweak via env var (default ON)
        private static readonly bool ToneBoostEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_TONEMAP"), "0", StringComparison.OrdinalIgnoreCase);

        // -------------------------
        // Public API (unchanged)
        // -------------------------

        /// <summary>
        /// Capture the foreground window (client area) if activeWindow=true,
        /// otherwise capture the primary monitor.
        /// </summary>
        public static Bitmap Capture(bool activeWindow)
        {
            if (activeWindow)
            {
                var hwnd = GetForegroundWindow();
                return Capture(hwnd);
            }
            else
            {
                return CapturePrimaryMonitor();
            }
        }

        /// <summary>
        /// Capture the client area of the given window handle (HWND). If hwnd == IntPtr.Zero,
        /// captures the foreground window client. Falls back to whole primary monitor if needed.
        /// </summary>
        public static Bitmap Capture(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                hwnd = GetForegroundWindow();

            if (TryGetClientRectOnScreen(hwnd, out var r))
            {
                return CopyScreenRect(r);
            }

            // Fallback: whole primary monitor
            return CapturePrimaryMonitor();
        }

        /// <summary>
        /// Capture a normalized crop (x,y,w,h are in 0..1 relative to the window client size).
        /// </summary>
        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double x, double y, double w, double h)
        {
            using var baseBmp = Capture(hwnd);
            int cx = Clamp((int)Math.Round(x * baseBmp.Width),  0, baseBmp.Width  - 1);
            int cy = Clamp((int)Math.Round(y * baseBmp.Height), 0, baseBmp.Height - 1);
            int cw = Clamp((int)Math.Round(w * baseBmp.Width),  1, baseBmp.Width  - cx);
            int ch = Clamp((int)Math.Round(h * baseBmp.Height), 1, baseBmp.Height - cy);

            var rect = new Rectangle(cx, cy, cw, ch);
            var crop = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(crop))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(baseBmp, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
            }

            if (ToneBoostEnabled) GentleContrastBoostInPlace(crop);
            return crop;
        }

        /// <summary>
        /// Encode a bitmap to JPEG bytes with the given quality (10..100).
        /// </summary>
        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            using var ms = new MemoryStream();
            var enc = GetJpegEncoder();
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, Clamp(quality, 10, 100));
            bmp.Save(ms, enc, encParams);
            return ms.ToArray();
        }

        // -------------------------
        // Internal helpers
        // -------------------------

        private static Bitmap CapturePrimaryMonitor()
        {
            var scr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            return CopyScreenRect(new RECT(scr.Left, scr.Top, scr.Right, scr.Bottom));
        }

        private static Bitmap CopyScreenRect(RECT r)
        {
            int w = Math.Max(1, r.right  - r.left);
            int h = Math.Max(1, r.bottom - r.top);

            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(r.left, r.top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }

            if (ToneBoostEnabled) GentleContrastBoostInPlace(bmp);
            return bmp;
        }

        private static bool TryGetClientRectOnScreen(IntPtr hwnd, out RECT rectScreen)
        {
            rectScreen = default;
            if (hwnd == IntPtr.Zero) return false;

            if (!GetClientRect(hwnd, out var rcClient)) return false;

            var pt = new POINT { X = rcClient.left, Y = rcClient.top };
            if (!ClientToScreen(hwnd, ref pt)) return false;

            rectScreen = new RECT(pt.X,
                                  pt.Y,
                                  pt.X + (rcClient.right  - rcClient.left),
                                  pt.Y + (rcClient.bottom - rcClient.top));
            return true;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var e in ImageCodecInfo.GetImageEncoders())
                if (e.MimeType == "image/jpeg") return e;
            throw new InvalidOperationException("JPEG encoder not found");
        }

        /// <summary>
        /// Light, safe tone/gamma tweak to improve text contrast for OCR. On good SDR,
        /// this is effectively a no-op; on flat/washed frames it pulls highlights down
        /// and darkens midtones slightly.
        /// </summary>
        private static void GentleContrastBoostInPlace(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride, h = bmp.Height, w = bmp.Width;
                int len = Math.Abs(stride) * h;
                var buf = new byte[len];
                Marshal.Copy(data.Scan0, buf, 0, len);

                // Quick brightness histogram for 95th percentile & mean
                var hist = new int[256];
                double sum = 0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 3;
                        byte b = buf[i + 0], g = buf[i + 1], r = buf[i + 2];
                        byte m = (byte)Math.Max(r, Math.Max(g, b));
                        hist[m]++; sum += (r + g + b) / 3.0;
                    }
                }
                int total = w * h;
                int acc = 0, p95 = 255, target = (int)(total * 0.95);
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) { p95 = i; break; } }
                double mean = sum / Math.Max(1, total);

                double scale = p95 > 235 ? 220.0 / p95 : 1.0; // compress highlights if blown
                double gamma = mean > 160 ? 1.25 : 1.0;       // darken a touch if too bright
                if (Math.Abs(scale - 1.0) < 0.01 && Math.Abs(gamma - 1.0) < 0.01) return;

                var lut = new byte[256];
                for (int i = 0; i < 256; i++)
                {
                    double v = i / 255.0;
                    v = Math.Pow(Math.Min(1.0, v * scale), gamma);
                    lut[i] = (byte)(v * 255.0 + 0.5);
                }

                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 3;
                        buf[i + 0] = lut[buf[i + 0]];
                        buf[i + 1] = lut[buf[i + 1]];
                        buf[i + 2] = lut[buf[i + 2]];
                    }
                }
                Marshal.Copy(buf, 0, data.Scan0, len);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        // -------------------------
        // Win32 P/Invoke (minimal)
        // -------------------------

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

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
