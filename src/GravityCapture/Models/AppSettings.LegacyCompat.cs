namespace GravityCapture.Models
{
    public partial class AppSettings
    {
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
