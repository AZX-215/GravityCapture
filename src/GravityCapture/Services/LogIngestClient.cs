using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    // Minimal, settings-aligned client. Extend later as needed.
    public static class LogIngestClient
    {
        private static string _baseUrl = "";
        private static string _apiKey  = "";

        public static void Configure(AppSettings s)
        {
            _baseUrl = s.ApiBaseUrl ?? s.RemoteOcrBaseUrl ?? "";
            _apiKey  = s.Auth?.ApiKey ?? s.RemoteOcrApiKey ?? "";
        }

        public static Task<(bool ok, string? error)> PostEventAsync(TribeEvent evt)
        {
            // Stage: no-op success. Wire real HTTP later.
            return Task.FromResult((true, (string?)null));
        }
    }
}
