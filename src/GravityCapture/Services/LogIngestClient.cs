using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    public static class LogIngestClient
    {
        private static readonly HttpClient _http = new HttpClient();

        private static string _baseUrl = ""; // without trailing slash, e.g. https://...railway.app
        private static string _key     = ""; // X-GL-Key

        public static void Configure(AppSettings settings)
        {
            var (url, key) = settings.GetActiveLogApi();
            _baseUrl = (url ?? "").TrimEnd('/');
            _key     = key ?? "";
        }

        private static HttpRequestMessage WithAuth(HttpRequestMessage req)
        {
            if (!string.IsNullOrEmpty(_key))
                req.Headers.Add("X-GL-Key", _key);
            return req;
        }

        public static async Task<(bool ok, string? error)> SendTestAsync()
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
                return (false, "No Log API URL configured.");

            var url = $"{_baseUrl}/api/tribe-events";
            var payload = new
            {
                server   = "NA-PVP-SmallTribes-TheCenter9306",
                tribe    = "Gravity",
                ark_day  = 6006,
                ark_time = "19:43:49",
                severity = "CRITICAL",
                category = "TAME_DEATH",
                actor    = "Your 49 DMG F - Lvl 362 (Argentavis)",
                message  = "Your 49 DMG F - Lvl 362 (Argentavis) was killed!",
                raw_line = "Day 6006, 19:43:49: Your 49 DMG F - Lvl 362 (Argentavis) was killed!"
            };

            try
            {
                using var req = WithAuth(new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(payload)
                });
                using var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                    return (false, $"HTTP {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static async Task<(bool ok, string? error)> PostEventAsync(TribeEvent evt)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
                return (false, "No Log API URL configured.");

            var url = $"{_baseUrl}/api/tribe-events";
            try
            {
                // ✅ Use generic overload (or named args) so options bind correctly.
                using var req = WithAuth(new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create<TribeEvent>(
                        evt,
                        options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                });

                using var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                    return (false, $"HTTP {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public sealed record RecentRow(
            long id,
            DateTime ingested_at,
            string severity,
            string category,
            string actor,
            string message
        );

        public static async Task<(bool ok, List<RecentRow>? rows, string? error)>
            GetRecentAsync(string server, string tribe, int limit = 25)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
                return (false, null, "No Log API URL configured.");

            var url = $"{_baseUrl}/api/tribe-events/recent?server={Uri.EscapeDataString(server)}&tribe={Uri.EscapeDataString(tribe)}&limit={limit}";

            try
            {
                using var req = WithAuth(new HttpRequestMessage(HttpMethod.Get, url));
                using var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                    return (false, null, $"HTTP {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");

                var rows = await res.Content.ReadFromJsonAsync<List<RecentRow>>();
                return (true, rows ?? new List<RecentRow>(), null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }
    }
}
