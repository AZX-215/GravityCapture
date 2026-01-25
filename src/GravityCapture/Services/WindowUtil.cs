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

        // Enumerate / title / visibility
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern int  GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int  GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

        // Hit-testing / ancestry / class
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        const uint GA_PARENT = 1;
        const uint GA_ROOT = 2;
        const uint GA_ROOTOWNER = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        public static bool TryGetClientBoundsOnScreen(IntPtr hwnd, out int x, out int y, out int w, out int h, out double scale)
        {
            x = y = w = h = 0; scale = 1.0;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetClientRect(hwnd, out var r)) return false;      // client size (px)
            var pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref pt)) return false;        // client origin (screen px)
            x = pt.X; y = pt.Y; w = r.Right - r.Left; h = r.Bottom - r.Top;
            var dpi = GetDpiForWindow(hwnd);
            scale = dpi > 0 ? dpi / 96.0 : 1.0;
            return w > 0 && h > 0;
        }

        /// Returns the top-level window at the given screen point.
        public static IntPtr GetTopLevelWindowFromPoint(int screenX, int screenY)
        {
            var pt = new POINT { X = screenX, Y = screenY };
            var h = WindowFromPoint(pt);
            if (h == IntPtr.Zero) return IntPtr.Zero;
            return GetAncestor(h, GA_ROOT);
        }

        /// Returns the title (caption) of a window, or empty if none.
        public static string GetWindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            _ = GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// Returns the Win32 class name (useful for debugging/identifying borderless games).
        public static string GetWindowClass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            _ = GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// Friendly debug string: "Title [Class]"
        public static string GetWindowDebugName(IntPtr hwnd)
            => $"{GetWindowTitle(hwnd)} [{GetWindowClass(hwnd)}]";

        /// Finds the best visible top-level window whose title contains the provided hint (case-insensitive).
        /// Picks the one with the largest client area if multiple match.
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
                if (len <= 0) return true; // many borderless games have blank titles; we’ll handle by hit-test path

                var sb = new StringBuilder(len + 1);
                _ = GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) return true;

                int x, y, w, h; double scale;
                if (TryGetClientBoundsOnScreen(hwnd, out x, out y, out w, out h, out scale))
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
