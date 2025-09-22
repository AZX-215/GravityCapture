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
        // Public API (unchanged)
        // -------------------------

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
            if (hwnd == IntPtr.Zero)
                hwnd = GetForegroundWindow();

            if (!TryGetClientRectOnScreen(hwnd, out var rect))
            {
                // Fallback to primary monitor dimensions
                rect = GetPrimaryMonitorRect();
            }

            int rw = rect.right - rect.left;
            int rh = rect.bottom - rect.top;

            int cx = rect.left + (int)Math.Round(x * rw);
            int cy = rect.top + (int)Math.Round(y * rh);
            int cw = Math.Max(1, (int)Math.Round(w * rw));
            int ch = Math.Max(1, (int)Math.Round(h * rh));

            var cropRect = new RECT(cx, cy, cx + cw, cy + ch);
            return CopyScreenRect(cropRect);
        }

        /// <summary>
        /// Encode a bitmap to JPEG bytes with the given quality (1..100).
        /// </summary>
        public static byte[] ToJpegBytes(Bitmap bmp, long quality = 90)
        {
            using var ms = new MemoryStream();
            var enc = GetEncoder(ImageFormat.Jpeg);
            var eps = new EncoderParameters(1);
            eps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            bmp.Save(ms, enc, eps);
            return ms.ToArray();
        }

        /// <summary>
        /// Dispose-safe helper.
        /// </summary>
        public static void DisposeBitmap(Bitmap? bmp)
        {
            if (bmp == null) return;
            try { bmp.Dispose(); } catch { }
        }

        // -------------------------
        // Internals
        // -------------------------

        private static Bitmap CopyScreenRect(RECT rect)
        {
            int w = Math.Max(1, rect.right - rect.left);
            int h = Math.Max(1, rect.bottom - rect.top);

            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);

            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.left, rect.top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }

            if (ToneBoostEnabled) GentleContrastBoostInPlace(bmp);
            return bmp;
        }

        private static Bitmap CapturePrimaryMonitor()
        {
            var r = GetPrimaryMonitorRect();
            return CopyScreenRect(r);
        }

        /// <summary>
        /// Crop helper that first captures, then crops in-memory to avoid multiple screen reads.
        /// </summary>
        public static Bitmap Crop(Bitmap baseBmp, Rectangle rect)
        {
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
        /// Lightweight, stable “tone boost”: pulls highlights down slightly and gives a mild S-curve.
        /// Implemented in-place, 24bpp path for speed. Safe for both SDR and HDR captures.
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

                unsafe
                {
                    byte* src = (byte*)data.Scan0;
                    for (int i = 0; i < len; i++) buf[i] = src[i];
                }

                // Simple per-channel curve: y = x*(1 + a*(x-0.5)) with clamp, a ~ -0.25
                // Then a small gamma ~ 0.95 to re-open shadows.
                const float a = -0.25f;
                const float inv255 = 1f / 255f;
                const float gamma = 0.95f;
                const float invGamma = 1f / gamma;

                for (int y = 0; y < h; y++)
                {
                    int row = y * Math.Abs(stride);
                    for (int x = 0; x < w; x++)
                    {
                        int idx = row + x * 3; // B,G,R
                        // B
                        float nb = buf[idx] * inv255;
                        nb = nb * (1f + a * (nb - 0.5f));
                        nb = MathF.Pow(Math.Clamp(nb, 0f, 1f), invGamma);
                        buf[idx] = (byte)Math.Clamp((int)(nb * 255f + 0.5f), 0, 255);
                        // G
                        float ng = buf[idx + 1] * inv255;
                        ng = ng * (1f + a * (ng - 0.5f));
                        ng = MathF.Pow(Math.Clamp(ng, 0f, 1f), invGamma);
                        buf[idx + 1] = (byte)Math.Clamp((int)(ng * 255f + 0.5f), 0, 255);
                        // R
                        float nr = buf[idx + 2] * inv255;
                        nr = nr * (1f + a * (nr - 0.5f));
                        nr = MathF.Pow(Math.Clamp(nr, 0f, 1f), invGamma);
                        buf[idx + 2] = (byte)Math.Clamp((int)(nr * 255f + 0.5f), 0, 255);
                    }
                }

                unsafe
                {
                    byte* dst = (byte*)data.Scan0;
                    for (int i = 0; i < len; i++) dst[i] = buf[i];
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        // -------------------------
        // Win32 helpers
        // -------------------------

        private static RECT GetPrimaryMonitorRect()
        {
            IntPtr hMonitor = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                return new RECT(mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right, mi.rcMonitor.bottom);
            }
            // Fallback if API fails
            return new RECT(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        }

        private static bool TryGetClientRectOnScreen(IntPtr hwnd, out RECT rectScreen)
        {
            rectScreen = default;

            if (hwnd == IntPtr.Zero)
                return false;

            if (!IsWindow(hwnd))
                return false;

            if (!IsWindowVisible(hwnd))
                return false;

            if (!GetClientRect(hwnd, out RECT rcClient))
                return false;

            // Map client (0,0) to screen
            POINT pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref pt))
                return false;

            rectScreen = new RECT(pt.X, pt.Y, pt.X + (rcClient.right - rcClient.left), pt.Y + (rcClient.bottom - rcClient.top));
            return true;
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageDecoders();
            foreach (var c in codecs)
            {
                if (c.FormatID == format.Guid) return c;
            }
            return ImageCodecInfo.GetImageDecoders()[1]; // JPEG fallback
        }

        // -------------------------
        // Win32 interop
        // -------------------------

        private const uint MONITOR_DEFAULTTOPRIMARY = 1;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

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
