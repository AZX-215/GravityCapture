using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using WF = System.Windows.Forms;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        /// <summary>
        /// Capture a screenshot.
        /// If activeWindowOnly = true, captures the foreground window.
        /// Otherwise, captures the PRIMARY monitor (not the whole virtual desktop).
        /// </summary>
        public static Bitmap Capture(bool activeWindowOnly)
        {
            Rectangle rect = activeWindowOnly ? GetActiveWindowRect() : GetPrimaryScreenRect();

            // Use a known pixel format to avoid ambiguous Bitmap ctor overloads.
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                                 new Size(rect.Width, rect.Height),
                                 CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        /// <summary>
        /// Encode a Bitmap to JPEG bytes with quality clamped to [40..100].
        /// </summary>
        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            using var ms = new MemoryStream();
            var enc = GetJpegEncoder();
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 40, 100));
            bmp.Save(ms, enc, ep);
            return ms.ToArray();
        }

        // --- helpers ---------------------------------------------------------

        // Primary monitor bounds (fixes “both monitors combined” issue)
        private static Rectangle GetPrimaryScreenRect()
        {
            return WF.Screen.PrimaryScreen.Bounds;
        }

        // Foreground window bounds; fallback to primary screen if unavailable
        private static Rectangle GetActiveWindowRect()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero || !GetWindowRect(h, out var r))
                return GetPrimaryScreenRect();
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            return ImageCodecInfo.GetImageEncoders()
                                 .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        }
    }
}
