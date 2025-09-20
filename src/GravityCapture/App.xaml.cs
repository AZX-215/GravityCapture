using System.Windows;
using GravityCapture.Services;  // + add

namespace GravityCapture
{
    // Force WPF Application, avoid WinForms ambiguity
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize OCR profile system early. Supports --profile=hdr|sdr and GC_PROFILE/GC_ENV.
            ProfileManager.Initialize(System.Environment.GetCommandLineArgs());  // + add

            base.OnStartup(e);

            // Nothing else needed here for OCR; OcrService reads env vars directly.
            // Keep StartupUri in App.xaml pointing to MainWindow.xaml, or
            // create/show your MainWindow here if you don’t use StartupUri.
            // var w = new MainWindow();
            // w.Show();
        }
    }
}
