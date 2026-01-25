// src/GravityCapture/Services/WindowTheme.cs
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GravityCapture.Services
{
    /// <summary>Forces dark title bars using DwmSetWindowAttribute.</summary>
    internal static class WindowTheme
    {
        // Windows 10 1809/1903 used 19; Win11 uses 20
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE     = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void ApplyDark(Window window)
        {
            if (window == null) return;

            // Ensure we have an HWND
            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            // Try modern attribute first, then fall back
            int enabled = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enabled, sizeof(int));
        }
    }
}
