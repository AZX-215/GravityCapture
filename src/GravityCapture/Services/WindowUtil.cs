using System;
using System.Runtime.InteropServices;

namespace GravityCapture.Services
{
    public static class WindowUtil
    {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] static extern int  GetDpiForWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        public static bool TryGetClientBoundsOnScreen(IntPtr hwnd, out int x, out int y, out int w, out int h, out double scale)
        {
            x = y = w = h = 0; scale = 1.0;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetClientRect(hwnd, out var r)) return false; // client size in physical px (PerMonitorV2)
            var pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref pt)) return false;   // top-left in screen coords (physical px)
            x = pt.X; y = pt.Y; w = r.Right - r.Left; h = r.Bottom - r.Top;
            var dpi = GetDpiForWindow(hwnd);
            scale = dpi > 0 ? dpi / 96.0 : 1.0; // informational; capture uses physical px already
            return w > 0 && h > 0;
        }
    }
}
