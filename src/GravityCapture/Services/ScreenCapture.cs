// src/GravityCapture/Services/ScreenCapture.cs
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace GravityCapture.Services
{
    /// <summary>
    /// Pure GDI capture. Works without Windows.Graphics.Capture.
    /// </summary>
    public static class ScreenCapture
    {
        // ---- Win32 ----
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

        /// <summary>Capture full primary desktop.</summary>
        public static Bitmap CaptureDesktopFull()
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;

            var hSrc  = GetWindowDC(IntPtr.Zero);
            var hDest = CreateCompatibleDC(hSrc);
            var hBmp  = CreateCompatibleBitmap(hSrc, bounds.Width, bounds.Height);
            var hOld  = SelectObject(hDest, hBmp);

            try
            {
                BitBlt(hDest, 0, 0, bounds.Width, bounds.Height, hSrc, bounds.Left, bounds.Top, SRCCOPY);
                return Image.FromHbitmap(hBmp);
            }
            finally
            {
                SelectObject(hDest, hOld);
                DeleteObject(hBmp);
                DeleteDC(hDest);
                ReleaseDC(IntPtr.Zero, hSrc);
            }
        }

        /// <summary>Capture a specific window. Tries PrintWindow then BitBlt fallback.</summary>
        public static Bitmap CaptureForPreview(IntPtr hwnd, out bool usedFallback)
        {
            usedFallback = false;

            if (hwnd == IntPtr.Zero) return CaptureDesktopFull();
            if (!GetWindowRect(hwnd, out var r)) return CaptureDesktopFull();

            int w = Math.Max(1, r.Right - r.Left);
            int h = Math.Max(1, r.Bottom - r.Top);

            var hSrc  = GetWindowDC(hwnd);
            var hDest = CreateCompatibleDC(hSrc);
            var hBmp  = CreateCompatibleBitmap(hSrc, w, h);
            var hOld  = SelectObject(hDest, hBmp);

            try
            {
                bool ok = PrintWindow(hwnd, hDest, 0);
                if (!ok)
                {
                    usedFallback = true;
                    ok = BitBlt(hDest, 0, 0, w, h, hSrc, 0, 0, SRCCOPY);
                }
                if (!ok) throw new InvalidOperationException("GDI capture failed.");

                return Image.FromHbitmap(hBmp);
            }
            finally
            {
                SelectObject(hDest, hOld);
                DeleteObject(hBmp);
                DeleteDC(hDest);
                ReleaseDC(hwnd, hSrc);
            }
        }

        // Back-compat shims
        public static Bitmap Capture()                                   => CaptureDesktopFull();
        public static Bitmap Capture(IntPtr hwnd)                        => CaptureForPreview(hwnd, out _);
        public static Bitmap Capture(IntPtr hwnd, out bool usedFallback) => CaptureForPreview(hwnd, out usedFallback);
    }
}
