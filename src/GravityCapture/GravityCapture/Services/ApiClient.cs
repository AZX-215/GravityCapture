using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services;

public sealed class ApiClient
{
    private readonly HttpClient _http = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task<string> SendIngestScreenshotAsync(byte[] pngBytes, AppSettings settings, CancellationToken ct)
    {
        var url = Combine(settings.ApiBaseUrl, "/ingest/screenshot");
        using var linked = CreateLinkedTimeout(ct, settings.RequestTimeoutSeconds);

        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(pngBytes ?? Array.Empty<byte>());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        content.Add(file, "image", "tribelog.png");

        content.Add(new StringContent(settings.ServerName ?? "unknown"), "server");
        content.Add(new StringContent(settings.TribeName ?? "unknown"), "tribe");

        // Desktop sets "post_visible=0" to avoid duplicating visibility logic client-side.
        content.Add(new StringContent("0"), "post_visible");

        // Client-side ping toggle (server still enforces its own CRITICAL_PING_ENABLED).
        content.Add(new StringContent(settings.CriticalPingEnabled ? "1" : "0"), "critical_ping");

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(settings.SharedSecret))
            req.Headers.TryAddWithoutValidation("X-GL-Key", settings.SharedSecret);

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token);
            var body = await resp.Content.ReadAsStringAsync(linked.Token);

            if (!resp.IsSuccessStatusCode)
                return $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(body)}";

            return Trim(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Timed out after {Math.Max(1, settings.RequestTimeoutSeconds)}s";
        }
        catch (Exception ex)
        {
            return $"Exception: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public async Task<string> ExtractAsync(byte[] pngBytes, AppSettings settings, CancellationToken ct)
    {
        var url = Combine(settings.ApiBaseUrl, "/extract");
        using var linked = CreateLinkedTimeout(ct, settings.RequestTimeoutSeconds);

        using var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(pngBytes ?? Array.Empty<byte>());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        content.Add(file, "image", "tribelog.png");

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(settings.SharedSecret))
            req.Headers.TryAddWithoutValidation("X-GL-Key", settings.SharedSecret);

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token);
            var body = await resp.Content.ReadAsStringAsync(linked.Token);

            if (!resp.IsSuccessStatusCode)
                return $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(body)}";

            return Trim(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Timed out after {Math.Max(1, settings.RequestTimeoutSeconds)}s";
        }
        catch (Exception ex)
        {
            return $"Exception: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static CancellationTokenSource CreateLinkedTimeout(CancellationToken ct, int timeoutSeconds)
    {
        var t = Math.Clamp(timeoutSeconds, 3, 300);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(t));
        return cts;
    }

    private static string Combine(string baseUrl, string path)
    {
        var b = (baseUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(b)) b = "http://localhost:8000";
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
