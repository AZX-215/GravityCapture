using System.Net.Http;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>Configures HttpClient for ingest API.</summary>
    public static class LogIngestClient
    {
        private static string _apiBase = "";
        private static string _apiKey  = "";
        private static string _channel = "";

        public static void Configure(AppSettings settings)
        {
            _apiBase = (settings.ApiBaseUrl ?? "").TrimEnd('/');
            _apiKey  = settings.Auth?.ApiKey ?? "";
            _channel = settings.Image?.ChannelId ?? "";
        }

        public static string ApiBase => _apiBase;

        public static HttpClient Create(bool includeAuth = true)
        {
            var c = new HttpClient();
            if (includeAuth)
            {
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    c.DefaultRequestHeaders.Add("X-GL-Key", _apiKey);
                    c.DefaultRequestHeaders.Add("x-api-key", _apiKey); // tolerant
                }
                if (!string.IsNullOrEmpty(_channel))
                    c.DefaultRequestHeaders.Add("X-GL-Channel", _channel);
            }
            return c;
        }
    }
}
