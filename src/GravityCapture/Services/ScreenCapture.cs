#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;

namespace GravityCapture.Services
{
    internal static class ScreenCapture
    {
        // ---------------- public surface ----------------

        public static IntPtr ResolveArkWindow()
        {
            // Try common processes/titles. Non-fatal if not found.
            foreach (var p in Process.GetProcesses())
            {
                string name = p.ProcessName.ToLowerInvariant();
                if (name.Contains("ark") || name.Contains("ascended") || name.Contains("shootergame"))
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        return p.MainWindowHandle;
                }
            }
            return IntPtr.Zero;
        }

        public static (bool ok, Rectangle rect, IntPtr hwndUsed) SelectRegion(IntPtr preferredHwnd)
        {
            // Keep API shape. If your overlay window exists, call it here.
            // For now, return "canceled" so the app remains responsive.
            return (false, Rectangle.Empty, preferredHwnd);
        }

        public static bool TryNormalizeRect(IntPtr hwnd, Rectangle selection,
            out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;

            if (hwnd == IntPtr.Zero) return false;
            if (!GetWindowRect(hwnd, out RECT wr)) return false;

            double w = wr.Width;
            double h = wr.Height;
            if (w <= 0 || h <= 0) return false;

            // Clamp selection to the window bounds
            int x1 = Math.Max(selection.Left, wr.Left);
            int y1 = Math.Max(selection.Top, wr.Top);
            int x2 = Math.Min(selection.Right, wr.Right);
            int y2 = Math.Min(selection.Bottom, wr.Bottom);
            if (x2 <= x1 || y2 <= y1) return false;

            nx = (x1 - wr.Left) / w;
            ny = (y1 - wr.Top) / h;
            nw = (x2 - x1) / w;
            nh = (y2 - y1) / h;
            return true;
        }

        /// <summary>
        /// Returns a bitmap for preview. Today we always use the GDI path.
        /// </summary>
        public static Bitmap CaptureForPreview(IntPtr hwnd, out bool usedFallback, out string? failReason)
        {
            usedFallback = true;
            failReason = "CreateForWindow failed";
            return CaptureWindow(hwnd);
        }

        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double nx, double ny, double nw, double nh)
        {
            using Bitmap full = CaptureWindow(hwnd);

            int x = (int)Math.Round(nx * full.Width);
            int y = (int)Math.Round(ny * full.Height);
            int w = (int)Math.Round(nw * full.Width);
            int h = (int)Math.Round(nh * full.Height);

            x = Math.Clamp(x, 0, Math.Max(0, full.Width - 1));
            y = Math.Clamp(y, 0, Math.Max(0, full.Height - 1));
            w = Math.Clamp(w, 1, full.Width - x);
            h = Math.Clamp(h, 1, full.Height - y);

            var crop = new Rectangle(x, y, w, h);
            var bmp = new Bitmap(crop.Width, crop.Height, full.PixelFormat);
            using (var g = Graphics.FromImage(bmp))
            {
                g.DrawImage(full, new Rectangle(0, 0, bmp.Width, bmp.Height), crop, GraphicsUnit.Pixel);
            }
            return bmp;
        }

        // ---------------- GDI capture ----------------

        private static Bitmap CaptureWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("No target window.");

            if (!GetWindowRect(hwnd, out RECT r))
                throw new InvalidOperationException("GetWindowRect failed.");

            int width = r.Width;
            int height = r.Height;

            IntPtr hWndDC = GetWindowDC(hwnd);
            if (hWndDC == IntPtr.Zero)
                throw new InvalidOperationException("GetWindowDC failed.");

            IntPtr hMemDC = CreateCompatibleDC(hWndDC);
            IntPtr hBitmap = CreateCompatibleBitmap(hWndDC, width, height);
            IntPtr hOld = SelectObject(hMemDC, hBitmap);

            try
            {
                // Try PrintWindow for full client incl. off-screen parts; fallback to BitBlt.
                bool ok = PrintWindow(hwnd, hMemDC, 0x00000002 /* PW_RENDERFULLCONTENT */);
                if (!ok)
                {
                    ok = BitBlt(hMemDC, 0, 0, width, height, hWndDC, 0, 0, SRCCOPY);
                }

                if (!ok)
                    throw new InvalidOperationException("GDI capture failed.");

                var bmp = Image.FromHbitmap(hBitmap);
                return bmp;
            }
            finally
            {
                SelectObject(hMemDC, hOld);
                DeleteObject(hBitmap);
                DeleteDC(hMemDC);
                ReleaseDC(hwnd, hWndDC);
            }
        }

        // ---------------- Win32 interop ----------------

        private const int SRCCOPY = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(
            IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
            IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    }
}
