namespace GravityCapture.Models;

public sealed class NormalizedRect
{
    public double Left { get; set; } = 0.65;
    public double Top { get; set; } = 0.10;
    public double Width { get; set; } = 0.32;
    public double Height { get; set; } = 0.35;

    public void Clamp()
    {
        if (Left < 0) Left = 0;
        if (Top < 0) Top = 0;
        if (Width < 0) Width = 0;
        if (Height < 0) Height = 0;

        if (Left + Width > 1) Width = 1 - Left;
        if (Top + Height > 1) Height = 1 - Top;
        if (Width < 0) Width = 0;
        if (Height < 0) Height = 0;
    }

    public override string ToString()
        => $"L={Left:0.000}, T={Top:0.000}, W={Width:0.000}, H={Height:0.000}";
}
