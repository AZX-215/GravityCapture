namespace GravityCapture.Models
{
    // Keep ONLY the remote-toggle here so we don't duplicate properties.
    public partial class AppSettings
    {
        public bool UseRemoteOcr { get; set; } = true;
    }
}
