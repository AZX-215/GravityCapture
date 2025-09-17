using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdcDest,int nXDest,int nYDest,int nWidth,int nHeight,
                                                           IntPtr hdcSrc,int nXSrc,int nYSrc, int dwRop);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        const int SRCCOPY = 0x00CC0020;

        public static Bitmap Capture(bool activeWindowOnly)
        {
            if (activeWindowOnly)
            {
                var hwnd = WindowUtil.GetForegroundWindow();
                if (WindowUtil.TryGetClientBoundsOnScreen(hwnd, out int x, out int y, out int w, out int h, out _))
                    return CaptureScreenRect(new Rectangle(x, y, w, h));
            }
            // Fallback: entire primary screen
            var b = Screen.PrimaryScreen.Bounds;
            return CaptureScreenRect(b);
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double nx, double ny, double nw, double nh)
        {
            if (!WindowUtil.TryGetClientBoundsOnScreen(hwnd, out int x, out int y, out int w, out int h, out _))
                throw new InvalidOperationException("Could not get client bounds.");
            nx = Clamp01(nx); ny = Clamp01(ny); nw = Clamp01(nw); nh = Clamp01(nh);
            int cw = Math.Max(1, (int)Math.Round(w * nw));
            int ch = Math.Max(1, (int)Math.Round(h * nh));
            int cx = x + Math.Max(0, (int)Math.Round(w * nx));
            int cy = y + Math.Max(0, (int)Math.Round(h * ny));
            return CaptureScreenRect(new Rectangle(cx, cy, cw, ch));
        }

        public static byte[] ToJpegBytes(Bitmap bmp, int quality)
        {
            using var ms = new System.IO.MemoryStream();
            var enc = GetJpegEncoder();
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Max(1, Math.Min(100, quality)));
            bmp.Save(ms, enc, ep);
            return ms.ToArray();
        }

        private static Bitmap CaptureScreenRect(Rectangle r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            throw new Exception("JPEG encoder not found");
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
