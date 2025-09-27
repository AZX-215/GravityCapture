public sealed class WgcCapture : IDisposable
{
  public static bool IsSupported(); // OS >= 1903
  public static WgcCapture? TryStartForWindow(IntPtr hwnd, out string? why);
  public bool TryGetLatest(out System.Drawing.Bitmap? bmp, out string? why);
  public void Dispose();
}
