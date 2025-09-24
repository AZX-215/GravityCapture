using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    public sealed class RemoteOcrService : IDisposable
    {
        private readonly OcrClient _client;
        private readonly HttpClient _http;

        public RemoteOcrService(AppSettings settings, HttpClient? httpClient = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl))
                throw new InvalidOperationException("ApiBaseUrl is not configured.");
            if (settings.Auth == null || string.IsNullOrWhiteSpace(settings.Auth.SharedKey))
                throw new InvalidOperationException("Auth.SharedKey is not configured.");

            _http = httpClient ?? new HttpClient();
            _client = new OcrClient(settings.ApiBaseUrl.TrimEnd('/'), settings.Auth.SharedKey, _http);
        }

        public async Task<OcrClient.ExtractResponse> ExtractAsync(
            string imagePath,
            string contentType = "image/png",
            CancellationToken ct = default)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Image not found.", imagePath);

            await using var fs = File.OpenRead(imagePath);
            return await _client.ExtractAsync(fs, Path.GetFileName(imagePath), contentType, ct);
        }

        public void Dispose() => _http.Dispose();
    }
}
