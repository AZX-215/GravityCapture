#nullable enable
using System;
using System.Drawing;

namespace GravityCapture.Services
{
    /// <summary>
    /// Disabled WGC shim. Keeps call sites intact without any Windows.Graphics.Capture references.
    /// </summary>
    internal static class WgcCapture
    {
        public static bool IsSupported => false;

        public static Bitmap? TryCaptureWindow(IntPtr hwnd, out string? failReason)
        {
            failReason = "WGC disabled";
            return null;
        }
    }
}
