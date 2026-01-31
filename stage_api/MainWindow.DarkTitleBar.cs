using System;
using System.Windows;

namespace GravityCapture;

/// <summary>
/// Keeps the Windows 11 dark title bar applied reliably.
/// Wired from MainWindow.xaml via SourceInitialized.
/// </summary>
public partial class MainWindow : Window
{
    private void ApplyDarkTitleBar_OnSourceInitialized(object? sender, EventArgs e)
    {
        // Safe no-op on unsupported OS/builds.
        WindowsDarkTitleBar.TryEnable(this);
    }
}
