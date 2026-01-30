using System;

namespace GravityCapture.Models;

public sealed class NormalizedRect
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;

    public void Clamp()
    {
        Left = Math.Clamp(Left, 0, 1);
        Top = Math.Clamp(Top, 0, 1);
        Width = Math.Clamp(Width, 0, 1);
        Height = Math.Clamp(Height, 0, 1);

        if (Left + Width > 1) Width = 1 - Left;
        if (Top + Height > 1) Height = 1 - Top;

        Width = Math.Max(0, Width);
        Height = Math.Max(0, Height);
    }

    public override string ToString()
        => $"L={Left:0.000} T={Top:0.000} W={Width:0.000} H={Height:0.000}";
}
