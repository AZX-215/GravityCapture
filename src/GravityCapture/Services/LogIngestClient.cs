using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

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

    // For GET /api/tribe-events/recent
    public record TribeEventRow(
        int id,
        DateTimeOffset ingested_at,
        string server,
        string tribe,
        int ark_day,
        string ark_time,
        string severity,
        string category,
        string actor,
        string message
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
        /// POST a tribe event to the API configured in AppConfig.
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
        /// Adds a unique suffix to raw_line to avoid de-dup on repeated clicks (server will also de-dupe).
        /// </summary>
        public static Task<(bool ok, string? error)> SendTestAsync()
        {
            var suffix = DateTime.UtcNow.Ticks; // unique so you can insert repeatedly
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

        /// <summary>
        /// GET recent rows for a server/tribe from the stage API (no auth).
        /// limit clamped to 1..100 (default 20).
        /// </summary>
        public static async Task<(bool ok, List<TribeEventRow>? items, string? error)>
            GetRecentAsync(string? server, string? tribe, int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(AppConfig.ApiBaseUrl))
                return (false, null, "ApiBaseUrl is empty.");

            if (limit < 1 || limit > 100) limit = Math.Clamp(limit, 1, 100);

            var baseUrl = AppConfig.ApiBaseUrl.TrimEnd('/');
            var qs = new List<string>();
            if (!string.IsNullOrWhiteSpace(server)) qs.Add("server=" + Uri.EscapeDataString(server));
            if (!string.IsNullOrWhiteSpace(tribe))  qs.Add("tribe=" + Uri.EscapeDataString(tribe));
            qs.Add("limit=" + limit);
            var url = $"{baseUrl}/api/tribe-events/recent?{string.Join("&", qs)}";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var resp = await Http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return (false, null, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");

                var items = JsonSerializer.Deserialize<List<TribeEventRow>>(body, JsonOpts) ?? new();
                return (true, items, null);
            }
            catch (Exception ex)
            {
                return (false, null, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
