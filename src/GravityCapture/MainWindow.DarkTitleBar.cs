using System;
using System.Windows;
using System.Windows.Interop;

namespace GravityCapture;

/// <summary>
/// Keeps the Windows 11 dark title bar applied reliably.
/// Wired from MainWindow.xaml via SourceInitialized.
/// </summary>
public partial class MainWindow : Window
{
    private void ApplyDarkTitleBar_OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            // Signature in this repo: TryEnable(nint hwnd, bool enabled)
            WindowsDarkTitleBar.TryEnable((nint)hwnd, enabled: true);
        }
        catch
        {
            // Safe no-op on unsupported OS/builds.
        }
    }
}
