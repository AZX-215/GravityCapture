using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GravityCapture.Services
{
    /// <summary>
    /// Window/desktop capture helpers. Pure GDI fallback so it works without WGC.
    /// Includes shims to keep older call sites compiling.
    /// </summary>
    public static class ScreenCapture
    {
        #region Win32

        private const int SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy,
                                                                   IntPtr hdcSrc, int x1, int y1, int rop);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        #endregion

        /// <summary>
        /// Capture the full primary screen using GDI.
        /// </summary>
        public static Bitmap CaptureDesktopFull()
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            using var gDest = Graphics.FromHwnd(IntPtr.Zero);
            var hSrc = GetWindowDC(IntPtr.Zero);
            var hDest = CreateCompatibleDC(hSrc);
            var hBmp = CreateCompatibleBitmap(hSrc, screen.Width, screen.Height);
            var hOld = SelectObject(hDest, hBmp);

            BitBlt(hDest, 0, 0, screen.Width, screen.Height, hSrc, screen.Left, screen.Top, SRCCOPY);

            var bmp = Image.FromHbitmap(hBmp);

            SelectObject(hDest, hOld);
            DeleteObject(hBmp);
            DeleteDC(hDest);
            ReleaseDC(IntPtr.Zero, hSrc);

            return bmp;
        }

        /// <summary>
        /// Capture a specific window. Tries PrintWindow first, then copy from screen.
        /// </summary>
        public static Bitmap CaptureForPreview(IntPtr hwnd, out bool usedFallback)
        {
            usedFallback = false;

            if (hwnd == IntPtr.Zero)
                return CaptureDesktopFull();

            if (!GetWindowRect(hwnd, out var r))
                return CaptureDesktopFull();

            int w = Math.Max(1, r.Right - r.Left);
            int h = Math.Max(1, r.Bottom - r.Top);

            var hSrc = GetWindowDC(hwnd);
            var hDest = CreateCompatibleDC(hSrc);
            var hBmp = CreateCompatibleBitmap(hSrc, w, h);
            var hOld = SelectObject(hDest, hBmp);

            try
            {
                // Try PrintWindow first (works for many windows that BitBlt cannot reach)
                bool ok = PrintWindow(hwnd, hDest, 0);
                if (!ok)
                {
                    usedFallback = true;
                    ok = BitBlt(hDest, 0, 0, w, h, hSrc, 0, 0, SRCCOPY);
                }

                if (!ok)
                    throw new InvalidOperationException("GDI capture failed.");

                var bmp = Image.FromHbitmap(hBmp);
                return bmp;
            }
            finally
            {
                SelectObject(hDest, hOld);
                DeleteObject(hBmp);
                DeleteDC(hDest);
                ReleaseDC(hwnd, hSrc);
            }
        }

        // ---------------- Back-compat shims (keep existing call-sites compiling) ----------------

        public static Bitmap Capture()                                  => CaptureDesktopFull();
        public static Bitmap Capture(IntPtr hwnd)                       => CaptureForPreview(hwnd, out _);
        public static Bitmap Capture(IntPtr hwnd, out bool usedFallback) => CaptureForPreview(hwnd, out usedFallback);
    }
}
