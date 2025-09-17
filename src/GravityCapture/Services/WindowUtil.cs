using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GravityCapture.Services
{
    public static class WindowUtil
    {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] static extern int  GetDpiForWindow(IntPtr hWnd);

        // Find by title
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        public static bool TryGetClientBoundsOnScreen(IntPtr hwnd, out int x, out int y, out int w, out int h, out double scale)
        {
            x = y = w = h = 0; scale = 1.0;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetClientRect(hwnd, out var r)) return false;
            var pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref pt)) return false;
            x = pt.X; y = pt.Y; w = r.Right - r.Left; h = r.Bottom - r.Top;
            var dpi = GetDpiForWindow(hwnd);
            scale = dpi > 0 ? dpi / 96.0 : 1.0;
            return w > 0 && h > 0;
        }

        /// <summary>
        /// Finds the best visible top-level window whose title contains the provided hint (case-insensitive).
        /// Picks the one with the largest client area if multiple match.
        /// Returns IntPtr.Zero if none found.
        /// </summary>
        public static IntPtr FindBestWindowByTitleHint(string? hint)
        {
            if (string.IsNullOrWhiteSpace(hint)) return IntPtr.Zero;
            string needle = hint.Trim();
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                int len = GetWindowTextLength(hwnd);
                if (len <= 0) return true;

                var sb = new StringBuilder(len + 1);
                _ = GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) return true;

                if (TryGetClientBoundsOnScreen(hwnd, out _, out _, out int w, out int h, out _))
                {
                    long area = (long)w * h;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = hwnd;
                    }
                }
                return true;
            }, IntPtr.Zero);

            return best;
        }
    }
}
