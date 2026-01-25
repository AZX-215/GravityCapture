// src/GravityCapture/Services/ScreenCapture.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GravityCapture.Services
{
    /// <summary>
    /// Pure GDI capture (no WGC). Provides PrintWindow/BitBlt capture for a window
    /// plus helpers for full-desktop and normalized crop.
    /// </summary>
    public static class ScreenCapture
    {
        // ---- Win32 ----
        private const int SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")] private static extern bool BitBlt(
            IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>Capture the full primary desktop.</summary>
        public static Bitmap CaptureDesktopFull()
        {
            // PrimaryScreen is effectively non-null in WinForms; use ! to silence nullable warnings.
            var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;

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

        /// <summary>
        /// Capture a specific window for preview. Tries PrintWindow first, then falls back to BitBlt.
        /// Returns the captured bitmap and tells whether the fallback path was used.
        /// </summary>
        public static Bitmap CaptureForPreview(IntPtr hwnd, out bool usedFallback)
        {
            usedFallback = false;

            if (hwnd == IntPtr.Zero) return CaptureDesktopFull();
            if (!GetWindowRect(hwnd, out var r)) return CaptureDesktopFull();

            int w = Math.Max(1, r.Right  - r.Left);
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

        /// <summary>
        /// Capture a window and crop by normalized rectangle (0..1) relative to the captured image.
        /// </summary>
        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double nx, double ny, double nw, double nh)
        {
            using var full = CaptureForPreview(hwnd, out _);

            int W = Math.Max(1, full.Width);
            int H = Math.Max(1, full.Height);

            int x = Clamp((int)Math.Round(nx * W), 0, W - 1);
            int y = Clamp((int)Math.Round(ny * H), 0, H - 1);
            int w = Clamp((int)Math.Round(nw * W), 1, W - x);
            int h = Clamp((int)Math.Round(nh * H), 1, H - y);

            var rect = new Rectangle(x, y, w, h);
            return full.Clone(rect, full.PixelFormat);
        }

        // Back-compat shims to match older callers
        public static Bitmap Capture()                                   => CaptureDesktopFull();
        public static Bitmap Capture(IntPtr hwnd)                        => CaptureForPreview(hwnd, out _);
        public static Bitmap Capture(IntPtr hwnd, out bool usedFallback) => CaptureForPreview(hwnd, out usedFallback);
    }
}
