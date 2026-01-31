using System;
using System.Windows;
using System.Windows.Interop;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        // Hooked up from MainWindow.xaml: SourceInitialized="Window_SourceInitialized"
        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                WindowsDarkTitleBar.TryEnable(new WindowInteropHelper(this).Handle, enabled: true);
            }
            catch
            {
                // Ignore - if DWM call fails, app still runs normally.
            }
        }
    }
}
