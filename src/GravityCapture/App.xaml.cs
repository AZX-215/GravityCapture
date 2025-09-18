using System.Windows;

namespace GravityCapture
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Optional runtime toggles:
            // System.Environment.SetEnvironmentVariable("GC_DEBUG_OCR", "1");
            // System.Environment.SetEnvironmentVariable("GC_OCR_TONEMAP", "1");   // or "0"
            // System.Environment.SetEnvironmentVariable("GC_OCR_ADAPTIVE", "1");  // or "0"
            // System.Environment.SetEnvironmentVariable("GC_ENV", "Stage");       // optional

            base.OnStartup(e);
        }
    }
}
