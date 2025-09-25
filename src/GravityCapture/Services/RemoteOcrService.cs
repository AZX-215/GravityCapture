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
    public sealed class RemoteOcrService : IDisposable
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

        public RemoteOcrService(AppSettings settings)
        {
            if (settings is null) throw new ArgumentNullException(nameof(settings));

            var baseUrl = (settings.ApiBaseUrl ?? settings.RemoteOcrBaseUrl ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("ApiBaseUrl is empty.");

            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            var apiKey = settings.Auth?.ApiKey ?? settings.RemoteOcrApiKey ?? "";
            if (!string.IsNullOrEmpty(apiKey))
                _http.DefaultRequestHeaders.Add("ApiKey", apiKey);
        }

        public async Task<ExtractResponse> ExtractAsync(Stream image, CancellationToken ct = default)
        {
            using var form = new MultipartFormDataContent();
            var sc = new StreamContent(image);
            sc.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(sc, "file", "capture.png");

            using var res = await _http.PostAsync("/api/ocr/extract", form, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            await using var s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<ExtractResponse>(s, _json, ct).ConfigureAwait(false);
            if (payload is null) throw new InvalidOperationException("Empty OCR response.");
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
