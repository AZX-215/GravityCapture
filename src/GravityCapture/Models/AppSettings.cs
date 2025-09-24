namespace GravityCapture.Models
{
    public sealed class AppSettings
    {
        // Back-compat properties
        public string? ApiBaseUrl { get; set; }
        public AuthSettings? Auth { get; set; }
        public bool UseRemoteOcr { get; set; } = true;

        // Optional new, namespaced config
        public RemoteOcrConfig? RemoteOcr { get; set; }

        public sealed class AuthSettings
        {
            public string? SharedKey { get; set; }
        }

        public sealed class RemoteOcrConfig
        {
            public string? BaseUrl { get; set; }
            public string? SharedKey { get; set; }
            public string? DefaultEngine { get; set; }
        }
    }
}
