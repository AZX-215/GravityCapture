using System;
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

        public ApiClient(AppSettings settings, HttpClient? http = null)
        {
            _s = settings;
            _http = http ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(20);

            // Auth headers. Send both to be tolerant.
            var key = _s.Auth?.ApiKey ?? string.Empty;
            var chan = _s.Image?.ChannelId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!_http.DefaultRequestHeaders.Contains("X-GL-Key"))
                    _http.DefaultRequestHeaders.Add("X-GL-Key", key);
                if (!_http.DefaultRequestHeaders.Contains("x-api-key"))
                    _http.DefaultRequestHeaders.Add("x-api-key", key);
            }
            if (!string.IsNullOrWhiteSpace(chan))
            {
                if (!_http.DefaultRequestHeaders.Contains("X-GL-Channel"))
                    _http.DefaultRequestHeaders.Add("X-GL-Channel", chan);
            }
        }

        private string Combine(string path)
        {
            var root = _s.ApiBaseUrl ?? "";
            if (string.IsNullOrWhiteSpace(root)) return path;
            root = root.TrimEnd('/');
            path = path.TrimStart('/');
            return $"{root}/{path}";
        }

        public async Task<(bool ok, string body)> PostScreenshotAsync(byte[] jpegBytes, bool postVisible)
        {
            var url = Combine("/ingest/screenshot");
            using var content = new MultipartFormDataContent();
            var img = new ByteArrayContent(jpegBytes);
            img.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(img, "file", "visible.jpg");

            content.Add(new StringContent(_s.Capture?.ServerName ?? _s.ServerName ?? string.Empty), "server");
            content.Add(new StringContent(_s.TribeName ?? string.Empty), "tribe");
            content.Add(new StringContent(postVisible ? "1" : "0"), "post_visible");

            using var res = await _http.PostAsync(url, content).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (res.IsSuccessStatusCode, body);
        }

        public async Task<(bool ok, string body)> OcrOnlyAsync(byte[] jpegBytes)
        {
            // Try /ocr then fallback /extract
            foreach (var path in new[] { "/ocr", "/extract" })
            {
                try
                {
                    var url = Combine(path);
                    using var content = new MultipartFormDataContent();
                    var img = new ByteArrayContent(jpegBytes);
                    img.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    content.Add(img, "file", "crop.jpg");

                    content.Add(new StringContent(_s.Capture?.ServerName ?? _s.ServerName ?? string.Empty), "server");
                    content.Add(new StringContent(_s.TribeName ?? string.Empty), "tribe");

                    using var res = await _http.PostAsync(url, content).ConfigureAwait(false);
                    var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (res.IsSuccessStatusCode) return (true, body);
                }
                catch { /* try next */ }
            }

            return (false, "{\"error\":\"OCR failed\"}");
        }

        public async Task<(bool ok, string body)> SendPastedLineAsync(string line)
        {
            var url = Combine("/ingest/log-line");
            var payload = new
            {
                line,
                server = _s.Capture?.ServerName ?? _s.ServerName ?? string.Empty,
                tribe = _s.TribeName ?? string.Empty
            };
            var json = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await _http.PostAsync(url, json).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (res.IsSuccessStatusCode, body);
        }

        public void Dispose() => _http.Dispose();
    }
}
