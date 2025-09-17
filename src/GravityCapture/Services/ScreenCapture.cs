using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Point = System.Drawing.Point;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        /// <summary>
        /// Capture either the active window's client area (preferred) or the whole virtual desktop.
        /// </summary>
        public static Bitmap Capture(bool activeWindowOnly)
        {
            if (activeWindowOnly)
            {
                var hwnd = WindowUtil.GetForegroundWindow();
                if (hwnd != IntPtr.Zero &&
                    WindowUtil.TryGetClientBoundsOnScreen(hwnd, out int x, out int y, out int w, out int h, out _))
                {
                    return CopyFromScreenRect(new Rectangle(x, y, w, h));
                }
                // If we can't resolve the foreground window, fall back to the full desktop below.
            }

            // Multi-monitor safe: use the virtual desktop rectangle.
            var vs = SystemInformation.VirtualScreen;
            var rect = vs.Width > 0 && vs.Height > 0 ? vs : new Rectangle(0, 0, 1920, 1080);
            return CopyFromScreenRect(rect);
        }

        /// <summary>
        /// Capture a normalized (0..1) crop of a window's client area; falls back to the virtual screen if needed.
        /// </summary>
        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double nx, double ny, double nw, double nh)
        {
            // Try the target window's client bounds first
            if (WindowUtil.TryGetClientBoundsOnScreen(hwnd, out int cx, out int cy, out int cw, out int ch, out _) &&
                cw > 0 && ch > 0)
            {
                int x = cx + Clamp((int)Math.Round(nx * cw), 0, cw - 1);
                int y = cy + Clamp((int)Math.Round(ny * ch), 0, ch - 1);
                int w = Clamp((int)Math.Round(nw * cw), 1, cw - (x - cx));
                int h = Clamp((int)Math.Round(nh * ch), 1, ch - (y - cy));

                return CopyFromScreenRect(new Rectangle(x, y, w, h));
            }

            // Fallback: normalized crop of the virtual desktop
            var vs = SystemInformation.VirtualScreen;
            int vx = vs.X, vy = vs.Y, vw = Math.Max(1, vs.Width), vh = Math.Max(1, vs.Height);

            int sx = vx + Clamp((int)Math.Round(nx * vw), 0, vw - 1);
            int sy = vy + Clamp((int)Math.Round(ny * vh), 0, vh - 1);
            int sw = Clamp((int)Math.Round(nw * vw), 1, vw - (sx - vx));
            int sh = Clamp((int)Math.Round(nh * vh), 1, vh - (sy - vy));

            return CopyFromScreenRect(new Rectangle(sx, sy, sw, sh));
        }

        /// <summary>
        /// Encode a bitmap to JPEG with quality (10..100). Falls back to PNG if JPEG codec is unavailable.
        /// </summary>
        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            int q = Math.Clamp(quality, 10, 100);
            using var ms = new MemoryStream();

            var enc = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.MimeType == "image/jpeg");
            if (enc == null)
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }

            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)q);
            bmp.Save(ms, enc, ep);
            return ms.ToArray();
        }

        // ----- helpers -----

        private static Bitmap CopyFromScreenRect(Rectangle r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(new Point(r.X, r.Y), Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
