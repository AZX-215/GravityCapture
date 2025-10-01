using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>Path-aware HTTP client that tries common Railway stage routes and headers.</summary>
    public sealed class ApiClient2 : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
        private readonly AppSettings _s;

        public ApiClient2(AppSettings s)
        {
            _s = s;
            var key = _s.Auth?.ApiKey ?? "";
            if (!string.IsNullOrWhiteSpace(key))
            {
                // try both header spellings
                _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", key);
                _http.DefaultRequestHeaders.TryAddWithoutValidation("ApiKey", key);
                _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GL-Key", key);
            }
            var chan = _s.Image?.ChannelId ?? "";
            if (!string.IsNullOrWhiteSpace(chan))
                _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GL-Channel", chan);
        }

        private string Url(string path)
        {
            var root = _s.ApiBaseUrl ?? "";
            root = root.TrimEnd('/');
            path = path.TrimStart('/');
            return $"{root}/{path}";
        }

        public async Task<(bool ok, string body)> OcrOnlyAsync(byte[] jpegBytes)
        {
            var candidates = new[]
            {
                _s.OcrPath,
                "/extract",
                "/api/extract",
                "/ocr",
                "/api/ocr",
                "/v1/extract",
                "/ocr/extract"
            };

            foreach (var p in candidates)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    using var content = new MultipartFormDataContent();
                    var img = new ByteArrayContent(jpegBytes);
                    img.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    content.Add(img, "file", "crop.jpg");
                    content.Add(new StringContent(_s.Capture?.ServerName ?? _s.ServerName ?? string.Empty), "server");
                    content.Add(new StringContent(_s.TribeName ?? string.Empty), "tribe");

                    var res = await _http.PostAsync(Url(p), content).ConfigureAwait(false);
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (res.IsSuccessStatusCode) return (true, body);
                    if (res.StatusCode == HttpStatusCode.NotFound || res.StatusCode == HttpStatusCode.MethodNotAllowed) continue;
                    return (false, body);
                }
                catch (Exception ex) { return (false, ex.Message); }
            }
            return (false, "{\"error\":\"OCR endpoint not found\"}");
        }

        public async Task<(bool ok, string body)> PostScreenshotAsync(byte[] jpegBytes, bool postVisible)
        {
            try
            {
                var path = string.IsNullOrWhiteSpace(_s.ScreenshotIngestPath) ? "/ingest/screenshot" : _s.ScreenshotIngestPath!;
                using var content = new MultipartFormDataContent();
                var img = new ByteArrayContent(jpegBytes);
                img.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                content.Add(img, "file", "visible.jpg");
                content.Add(new StringContent(_s.Capture?.ServerName ?? _s.ServerName ?? string.Empty), "server");
                content.Add(new StringContent(_s.TribeName ?? string.Empty), "tribe");
                content.Add(new StringContent(postVisible ? "1" : "0"), "post_visible");

                var res = await _http.PostAsync(Url(path), content).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                return (res.IsSuccessStatusCode, body);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public async Task<(bool ok, string body)> SendPastedLineAsync(string line)
        {
            try
            {
                var path = string.IsNullOrWhiteSpace(_s.LogLineIngestPath) ? "/ingest/log-line" : _s.LogLineIngestPath!;
                var payload = new
                {
                    line,
                    server = _s.Capture?.ServerName ?? _s.ServerName ?? string.Empty,
                    tribe = _s.TribeName ?? string.Empty
                };
                var res = await _http.PostAsync(Url(path),
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                return (res.IsSuccessStatusCode, body);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public void Dispose() => _http.Dispose();
    }
}
