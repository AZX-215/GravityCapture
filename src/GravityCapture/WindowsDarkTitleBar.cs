using System;
using System.Runtime.InteropServices;

namespace GravityCapture;

internal static class WindowsDarkTitleBar
{
    // Attribute 20 works on Win10 1903+ / Win11. Attribute 19 is used on some older builds.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static void TryEnable(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero) return;

        int useDark = enabled ? 1 : 0;

        // Try both attribute IDs; unsupported calls are harmless (DWM returns a failure HRESULT).
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
    }
}
