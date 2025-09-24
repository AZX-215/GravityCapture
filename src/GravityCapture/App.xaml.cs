using System;
using System.Windows;
using GravityCapture.Services;

namespace GravityCapture
{
    // WPF entry point
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize OCR profile system early.
            // Supports --profile=hdr|sdr and GC_PROFILE / GC_ENV env vars.
            ProfileManager.Initialize(Environment.GetCommandLineArgs());

            base.OnStartup(e);
            // If you’re not using StartupUri in App.xaml, you could show MainWindow here:
            // new MainWindow().Show();
        }
    }
}
