using System.Net.Http;

using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>Lightweight holder for API base and key. Creates HttpClient with auth header on demand.</summary>
    public static class LogIngestClient
    {
        private static string _apiBase = "";
        private static string _apiKey  = "";

        public static void Configure(AppSettings settings)
        {
            _apiBase = (settings.ApiBaseUrl ?? "").TrimEnd('/');
            _apiKey  = settings.Auth?.ApiKey ?? "";
        }

        public static string ApiBase => _apiBase;

        public static HttpClient Create(bool includeAuth = true)
        {
            var c = new HttpClient();
            if (includeAuth && !string.IsNullOrEmpty(_apiKey))
                c.DefaultRequestHeaders.Add("ApiKey", _apiKey);
            return c;
        }
    }
}
