using System;
using System.Net.Http;
using System.Text;

namespace GravityCapture.Services
{
    /// <summary>
    /// Lightweight HTTP logging that does NOT depend on Microsoft.Extensions.*.
    /// Enable body logging by setting env var GC_DEBUG_HTTP=1.
    /// </summary>
    public sealed class HttpLoggingHandler : DelegatingHandler
    {
        private readonly bool _logBodies =
            (Environment.GetEnvironmentVariable("GC_DEBUG_HTTP") ?? "0") == "1";

        public HttpLoggingHandler(HttpMessageHandler? inner = null)
            : base(inner ?? new HttpClientHandler()) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            Console.WriteLine($"[HTTP ➜] {req.Method} {req.RequestUri}");

            if (_logBodies && req.Content != null)
            {
                var preview = await SafeRead(req.Content, ct);
                Console.WriteLine($"[HTTP ➜] body({preview.Length}): {Trim8k(preview)}");
            }

            HttpResponseMessage resp;
            try
            {
                resp = await base.SendAsync(req, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HTTP ✖] " + ex.Message);
                throw;
            }

            var body = resp.Content != null ? await SafeRead(resp.Content, ct) : "";
            Console.WriteLine($"[HTTP ⇦] {(int)resp.StatusCode} {resp.ReasonPhrase}");
            if (!resp.IsSuccessStatusCode || _logBodies)
                Console.WriteLine($"[HTTP ⇦] body({body.Length}): {Trim8k(body)}");

            return resp;
        }

        private static async Task<string> SafeRead(HttpContent content, CancellationToken ct)
        {
            try
            {
                var bytes = await content.ReadAsByteArrayAsync(ct);
                if (bytes.Length == 0) return "";
                // Assume UTF-8 for diagnostics; trim to 8KB so logs don’t explode
                var max = Math.Min(bytes.Length, 8 * 1024);
                return Encoding.UTF8.GetString(bytes, 0, max);
            }
            catch
            {
                return "(unreadable content)";
            }
        }

        private static string Trim8k(string s)
            => s.Length <= 8192 ? s : s[..8192] + "…(trimmed)";
    }
}
