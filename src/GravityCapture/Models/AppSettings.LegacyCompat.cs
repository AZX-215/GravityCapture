// src/GravityCapture/Models/AppSettings.LegacyCompat.cs
namespace GravityCapture.Models
{
    public partial class AppSettings
    {
        // Legacy alias so older code that read _settings.TargetWindowHint still compiles.
        public string? TargetWindowHint
        {
            get => Capture?.TargetWindowHint ?? Image?.TargetWindowHint;
            set
            {
                Capture ??= new CaptureSettings();
                Image ??= new ImageSettings();
                Capture.TargetWindowHint = value;
                Image.TargetWindowHint = value;
            }
        }
    }
}
