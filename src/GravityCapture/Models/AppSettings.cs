namespace GravityCapture.Models
{
    // Base/settings portion (made partial to coexist with AppSettings.Remote.cs)
    public partial class AppSettings
    {
        public ApiConfig Api { get; set; } = new();
    }

    public sealed class ApiConfig
    {
        public string BaseUrl { get; set; } = "";
        public string? ApiKey { get; set; }
    }
}
