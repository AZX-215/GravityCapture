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
    public sealed class ApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly AppSettings _s;

        public ApiClient(AppSettings s, HttpClient? http = null)
        {
            _s = s;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

            var key = _s.Auth?.ApiKey ?? "";
            var chan = _s.Image?.ChannelId ?? "";
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!_http.DefaultRequestHeaders.Contains("X-GL-Key")) _http.DefaultRequestHeaders.Add("X-GL-Key", key);
                if (!_http.DefaultRequestHeaders.Contains("x-api-key")) _http.DefaultRequestHeaders.Add("x-api-key", key);
            }
            if (!string.IsNullOrWhiteSpace(chan) && !_http.DefaultRequestHeaders.Contains("X-GL-Channel"))
                _http.DefaultRequestHeaders.Add("X-GL-Channel", chan);
        }

        private string Url(string path)
        {
            var root = _s.ApiBaseUrl ?? "";
            root = root.TrimEnd('/');
            path = path.TrimStart('/');
            return $"{root}/{path}";
        }

        public async Task<(bool ok, string body)> PostScreenshotAsync(byte[] jpegBytes, bool postVisible)
        {
            try
            {
                var url = Url(_s.ScreenshotIngestPath ?? "/ingest/screenshot");
                using var content = new MultipartFormDataContent();
                var img = new ByteArrayContent(jpegBytes);
                img.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                content.Add(img, "file", "visible.jpg");
                content.Add(new StringContent(_s.Capture?.ServerName ?? _s.ServerName ?? string.Empty), "server");
                content.Add(new StringContent(_s.TribeName ?? string.Empty), "tribe");
                content.Add(new StringContent(postVisible ? "1" : "0"), "post_visible");

                var res = await _http.PostAsync(url, content).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                return (res.IsSuccessStatusCode, body);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<(bool ok, string body)> OcrOnlyAsync(byte[] jpegBytes)
        {
            var candidates = new[]
            {
                _s.OcrPath,                   // explicit override
                "/ocr",
                "/extract",
                "/api/ocr",
                "/api/extract",
                "/v1/ocr",
                "/v1/extract",
                "/ocr/extract",
                "/extract-text"
            };

            foreach (var p in candidates)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    var url = Url(p);
                    using var content = new MultipartFormDataContent();
                    var img = new ByteArrayContent(jpegBytes);
                    img.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    content.Add(img, "file", "crop.jpg");
                    content.Add(new StringContent(_s.Capture?.ServerName ?? _s.ServerName ?? string.Empty), "server");
                    content.Add(new StringContent(_s.TribeName ?? string.Empty), "tribe");

                    var res = await _http.PostAsync(url, content).ConfigureAwait(false);
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (res.IsSuccessStatusCode) return (true, body);

                    // If clearly not found, try next path.
                    if (res.StatusCode == HttpStatusCode.NotFound || res.StatusCode == HttpStatusCode.MethodNotAllowed)
                        continue;

                    // Other failures: return body so you see details.
                    return (false, body);
                }
                catch (Exception ex)
                {
                    // network or TLS error—bubble up
                    return (false, ex.Message);
                }
            }

            return (false, "{\"error\":\"OCR endpoint not found\"}");
        }

        public async Task<(bool ok, string body)> SendPastedLineAsync(string line)
        {
            try
            {
                var url = Url(_s.LogLineIngestPath ?? "/ingest/log-line");
                var payload = new
                {
                    line,
                    server = _s.Capture?.ServerName ?? _s.ServerName ?? string.Empty,
                    tribe = _s.TribeName ?? string.Empty
                };
                var res = await _http.PostAsync(url,
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                return (res.IsSuccessStatusCode, body);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
