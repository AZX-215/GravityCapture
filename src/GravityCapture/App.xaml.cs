using System;
using System.Windows;

namespace GravityCapture
{
    // Force WPF Application, avoid WinForms ambiguity
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Nothing else needed here for OCR; OcrService reads env vars directly.
            // Keep StartupUri in App.xaml pointing to MainWindow.xaml, or
            // create/show your MainWindow here if you don’t use StartupUri.
            // var w = new MainWindow();
            // w.Show();
        }
    }
}
