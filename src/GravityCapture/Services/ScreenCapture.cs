#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using GravityCapture.Windows; // RegionSelectorWindow (your existing overlay)

namespace GravityCapture.Services
{
    public static class ScreenCapture
    {
        // -------- window discovery --------
        public static IntPtr ResolveArkWindow()
        {
            IntPtr found = IntPtr.Zero;

            bool EnumWindowsProc(IntPtr h, IntPtr l)
            {
                var title = GetWindowText(h);
                if (title.Contains("ARK", StringComparison.OrdinalIgnoreCase))
                {
                    found = h;
                    return false;
                }
                return true;
            }

            EnumWindows(EnumWindowsProc, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero &&
                        p.ProcessName.Contains("Ark", StringComparison.OrdinalIgnoreCase))
                        return p.MainWindowHandle;
                }
                catch { /* ignore */ }
            }

            return IntPtr.Zero;
        }

        // -------- region selection (uses your window) --------
        public static (bool ok, Rectangle rect, IntPtr hwndUsed) SelectRegion(IntPtr hintHwnd)
        {
            var dlg = new RegionSelectorWindow(hintHwnd);
            var ok = dlg.ShowDialog() == true;
            return (ok, dlg.SelectedRect, dlg.CapturedHwnd);
        }

        // -------- normalization helpers --------
        public static bool TryNormalizeRect(IntPtr hwnd, Rectangle r, out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (hwnd == IntPtr.Zero || r.Width <= 0 || r.Height <= 0) return false;

            if (!GetWindowRect(hwnd, out var wr)) return false;
            int ww = Math.Max(1, wr.Right - wr.Left);
            int wh = Math.Max(1, wr.Bottom - wr.Top);

            int x = Math.Clamp(r.Left - wr.Left, 0, ww - 1);
            int y = Math.Clamp(r.Top - wr.Top, 0, wh - 1);
            int w = Math.Clamp(r.Width, 1, ww - x);
            int h = Math.Clamp(r.Height, 1, wh - y);

            nx = (double)x / ww;
            ny = (double)y / wh;
            nw = (double)w / ww;
            nh = (double)h / wh;
            return true;
        }

        public static bool TryNormalizeRectDesktop(Rectangle r, out double nx, out double ny, out double nw, out double nh)
        {
            nx = ny = nw = nh = 0;
            if (r.Width <= 0 || r.Height <= 0) return false;

            var ww = GetSystemMetrics(0);
            var wh = GetSystemMetrics(1);
            nx = (double)r.Left / ww;
            ny = (double)r.Top / wh;
            nw = (double)r.Width / ww;
            nh = (double)r.Height / wh;
            return true;
        }

        // -------- preview frame (BitBlt/PrintWindow path only) --------
        public static Bitmap CaptureForPreview(IntPtr hwnd, out bool usedFallback, out string? reason)
        {
            usedFallback = true;
            reason = "CreateForWindow failed";
            return Capture(hwnd);
        }

        // -------- crop using normalized rect --------
        public static Bitmap CaptureCropNormalized(IntPtr hwnd, double nx, double ny, double nw, double nh)
        {
            using var full = Capture(hwnd);

            int x = Math.Clamp((int)Math.Round(nx * full.Width), 0, full.Width - 1);
            int y = Math.Clamp((int)Math.Round(ny * full.Height), 0, full.Height - 1);
            int w = Math.Clamp((int)Math.Round(nw * full.Width), 1, full.Width - x);
            int h = Math.Clamp((int)Math.Round(nh * full.Height), 1, full.Height - y);

            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.DrawImage(full, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
            return bmp;
        }

        // -------- core window capture via PrintWindow -> BitBlt fallback --------
        public static Bitmap Capture(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var r)) throw new InvalidOperationException("Invalid window.");
            int w = Math.Max(1, r.Right - r.Left);
            int h = Math.Max(1, r.Bottom - r.Top);

            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            using var srcDc = new DeviceContext(GetWindowDC(hwnd), releaseWindow: true);
            using var memDc = new DeviceContext(CreateCompatibleDC(srcDc.Handle));

            using var hBmp = new GdiObject(CreateCompatibleBitmap(srcDc.Handle, w, h));
            var old = SelectObject(memDc.Handle, hBmp.Handle);

            // Try PrintWindow (full content) first
            const int PW_RENDERFULLCONTENT = 0x00000002;
            bool printed = PrintWindow(hwnd, memDc.Handle, PW_RENDERFULLCONTENT);

            // If PrintWindow fails, BitBlt visible area
            if (!printed)
            {
                const int SRCCOPY = 0x00CC0020;
                BitBlt(memDc.Handle, 0, 0, w, h, srcDc.Handle, 0, 0, SRCCOPY);
            }

            using (var tmp = Image.FromHbitmap(hBmp.Handle))
            {
                g.DrawImageUnscaled(tmp, 0, 0);
            }

            SelectObject(memDc.Handle, old);
            return bmp;
        }

        // -------- Win32 helpers --------
        private sealed class DeviceContext : IDisposable
        {
            public IntPtr Handle { get; }
            private readonly bool _releaseWindow;
            public DeviceContext(IntPtr h, bool releaseWindow = false)
            { Handle = h; _releaseWindow = releaseWindow; }
            public void Dispose()
            {
                if (Handle == IntPtr.Zero) return;
                if (_releaseWindow) ReleaseDC(IntPtr.Zero, Handle);
                else DeleteDC(Handle);
            }
        }

        private sealed class GdiObject : IDisposable
        {
            public IntPtr Handle { get; }
            public GdiObject(IntPtr h) { Handle = h; }
            public void Dispose()
            {
                if (Handle != IntPtr.Zero) DeleteObject(Handle);
            }
        }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]  private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]  private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]  private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [DllImport("gdi32.dll")]  private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [DllImport("gdi32.dll")]  private static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")]  private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);
        [DllImport("user32.dll")] private static extern bool EnumWindows(Func<IntPtr, IntPtr, bool> lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

        private static string GetWindowText(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            var sb = new System.Text.StringBuilder(len + 1);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private struct RECT { public int Left, Top, Right, Bottom; }
    }
}
