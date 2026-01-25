using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services;

public sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient()
    {
        // Ensure we fail fast instead of hanging forever when Railway / DNS / TLS stalls.
        const int timeoutSeconds = 60;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GravityCapture/1.0");
    }
public async Task<string> SendIngestScreenshotAsync(byte[] pngBytes, AppSettings settings, CancellationToken ct)
    {
        var url = Combine(settings.ApiBaseUrl, "/ingest/screenshot");

        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(pngBytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        content.Add(file, "image", "tribelog.png");

        content.Add(new StringContent(settings.ServerName ?? "unknown"), "server");
        content.Add(new StringContent(settings.TribeName ?? "unknown"), "tribe");
        content.Add(new StringContent("0"), "post_visible");

        // Client-side ping toggle (server still enforces its own CRITICAL_PING_ENABLED).
        content.Add(new StringContent(settings.CriticalPingEnabled ? "1" : "0"), "critical_ping");

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = content;

        if (!string.IsNullOrWhiteSpace(settings.SharedSecret))
            req.Headers.TryAddWithoutValidation("X-GL-Key", settings.SharedSecret);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(body)}";

        return Trim(body);
    }

    public async Task<string> ExtractAsync(byte[] pngBytes, AppSettings settings, CancellationToken ct)
    {
        var url = Combine(settings.ApiBaseUrl, "/extract");

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(pngBytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        content.Add(file, "image", "tribelog.png");

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = content;

        if (!string.IsNullOrWhiteSpace(settings.SharedSecret))
            req.Headers.TryAddWithoutValidation("X-GL-Key", settings.SharedSecret);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            return $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(body)}";

        return Trim(body);
    }

    private static string Combine(string baseUrl, string path)
    {
        var b = (baseUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(b))
            b = "http://localhost:8000";

        b = b.TrimEnd('/');
        path = path.StartsWith("/") ? path : "/" + path;
        return b + path;
    }

    private static string Trim(string s)
    {
        s ??= "";
        s = s.Trim();
        const int max = 2000;
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "…";
    }
}
