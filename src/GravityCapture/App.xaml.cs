using System.Windows;

namespace GravityCapture
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // If you want to force OCR debug flags at runtime, uncomment:
            // System.Environment.SetEnvironmentVariable("GC_DEBUG_OCR", "1");
            // System.Environment.SetEnvironmentVariable("GC_OCR_TONEMAP", "1");   // or "0"
            // System.Environment.SetEnvironmentVariable("GC_OCR_ADAPTIVE", "1");  // or "0"
            // System.Environment.SetEnvironmentVariable("GC_ENV", "Stage");       // optional override

            base.OnStartup(e);
        }
    }
}
