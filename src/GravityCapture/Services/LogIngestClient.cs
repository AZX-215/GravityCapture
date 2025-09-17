using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GravityCapture.Services
{
    public record TribeEvent(
        string server,
        string tribe,
        int ark_day,
        string ark_time,
        string severity,
        string category,
        string actor,
        string message,
        string raw_line
    );

    public static class LogIngestClient
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <summary>
        /// POSTS a tribe event to the API configured in AppConfig.
        /// Returns (ok, errorMessage).
        /// </summary>
        public static async Task<(bool ok, string? error)> PostEventAsync(TribeEvent evt)
        {
            if (string.IsNullOrWhiteSpace(AppConfig.ApiBaseUrl))
                return (false, "ApiBaseUrl is empty. Did GC_ENV=Stage load appsettings.Stage.json?");
            if (string.IsNullOrWhiteSpace(AppConfig.SharedKey))
                return (false, "SharedKey is empty. Check appsettings.Stage.json Auth:SharedKey.");

            var url = $"{AppConfig.ApiBaseUrl.TrimEnd('/')}/api/tribe-events";

            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(evt, JsonOpts), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-GL-Key", AppConfig.SharedKey);

            try
            {
                var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode) return (true, null);
                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
            }
            catch (Exception ex)
            {
                return (false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Convenience helper to send the same test payload you used from PowerShell.
        /// Adds a unique suffix to raw_line to avoid de-dup on repeated clicks.
        /// </summary>
        public static Task<(bool ok, string? error)> SendTestAsync()
        {
            var suffix = DateTime.UtcNow.Ticks; // unique so it inserts every time
            var evt = new TribeEvent(
                server:   "NA-PVP-SmallTribes-TheCenter9306",
                tribe:    "Gravity",
                ark_day:  6006,
                ark_time: "19:43:49",
                severity: "CRITICAL",
                category: "TAME_DEATH",
                actor:    "Your 43 WT F - Lvl 262 (Argentavis)",
                message:  "Your 43 WT F - Lvl 262 (Argentavis) was killed!",
                raw_line: $"Day 6006, 19:43:49: Your 43 WT F - Lvl 262 (Argentavis) was killed! (test {suffix})"
            );
            return PostEventAsync(evt);
        }
    }
}
