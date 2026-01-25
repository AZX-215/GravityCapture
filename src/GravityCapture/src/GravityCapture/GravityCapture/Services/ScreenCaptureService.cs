using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using GravityCapture.Models;
using Screen = System.Windows.Forms.Screen;

namespace GravityCapture.Services;

public sealed class ScreenCaptureService
{
    public sealed record CaptureResult(BitmapSource Preview, byte[] PngBytes, string ScreenName, Rectangle PixelRect);

    public CaptureResult Capture(AppSettings settings)
    {
        var screen = ResolveScreen(settings.CaptureScreenDeviceName);
        var b = screen.Bounds; // pixels

        var r = settings.CaptureRegion ?? new NormalizedRect();
        r.Clamp();

        var x = b.Left + (int)Math.Round(r.Left * b.Width);
        var y = b.Top + (int)Math.Round(r.Top * b.Height);
        var w = (int)Math.Round(r.Width * b.Width);
        var h = (int)Math.Round(r.Height * b.Height);

        // Safety: enforce minimum size and clamp to screen.
        w = Math.Max(50, Math.Min(w, b.Width));
        h = Math.Max(50, Math.Min(h, b.Height));
        if (x < b.Left) x = b.Left;
        if (y < b.Top) y = b.Top;
        if (x + w > b.Right) w = b.Right - x;
        if (y + h > b.Bottom) h = b.Bottom - y;

        var rect = new Rectangle(x, y, w, h);

        using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        }

        byte[] png;
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            png = ms.ToArray();
        }

        var preview = ToBitmapSource(bmp);

        return new CaptureResult(preview, png, screen.DeviceName, rect);
    }

    private static Screen ResolveScreen(string deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            foreach (var s in Screen.AllScreens)
            {
                if (string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return s;
            }
        }
        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }
}
