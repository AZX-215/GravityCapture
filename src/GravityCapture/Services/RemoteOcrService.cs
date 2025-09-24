using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    public sealed class RemoteOcrService
    {
        private readonly OcrClient _client;

        public RemoteOcrService(AppSettings settings, HttpClient? httpClient = null)
        {
            if (settings is null) throw new ArgumentNullException(nameof(settings));

            var baseUrl =
                settings.ApiBaseUrl ??
                settings.RemoteOcr?.BaseUrl ??
                throw new InvalidOperationException("OCR API base URL is not configured.");

            var key = settings.Auth?.SharedKey ?? settings.RemoteOcr?.SharedKey ?? string.Empty;

            _client = new OcrClient(baseUrl, key, httpClient);
        }

        // Back-compat: (path, CancellationToken)
        public Task<ExtractResponse> ExtractAsync(string imagePath, CancellationToken ct)
            => _client.ExtractAsync(imagePath, engine: null, ct);

        // Preferred: (path, engine?, CancellationToken)
        public Task<ExtractResponse> ExtractAsync(string imagePath, string? engine = null, CancellationToken ct = default)
            => _client.ExtractAsync(imagePath, engine, ct);
    }
}
