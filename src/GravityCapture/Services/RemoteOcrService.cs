using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Minimal helper the UI can new-up to call Remote OCR.
    /// No ILogger / DI to keep Stage app simple.
    /// </summary>
    public class RemoteOcrService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

        public RemoteOcrService(AppSettings settings)
        {
            var baseUrl = (settings?.ApiBaseUrl ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("ApiBaseUrl is empty.");

            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            var apiKey = settings?.Auth?.ApiKey;
            if (!string.IsNullOrEmpty(apiKey))
                _http.DefaultRequestHeaders.Add("ApiKey", apiKey);
        }

        public async Task<ExtractResponse> ExtractAsync(Stream image, CancellationToken ct = default)
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(image);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(streamContent, "file", "capture.png");

            using var res = await _http.PostAsync("/api/ocr/extract", content, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<ExtractResponse>(s, _json, ct).ConfigureAwait(false);
            if (payload == null) throw new InvalidOperationException("Empty OCR response.");
            return payload;
        }

        public async Task<ExtractResponse> ExtractAsync(string path, CancellationToken ct = default)
        {
            await using var fs = File.OpenRead(path);
            return await ExtractAsync(fs, ct).ConfigureAwait(false);
        }

        public void Dispose() => _http.Dispose();
    }
}
