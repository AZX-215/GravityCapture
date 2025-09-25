using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Calls the OCR API. Falls back: RemoteOcr* -> ApiBaseUrl/Auth.ApiKey.
    /// </summary>
    public sealed class RemoteOcrService
    {
        private readonly OcrClient _client;

        public RemoteOcrService(AppSettings settings)
        {
            if (settings is null) throw new ArgumentNullException(nameof(settings));

            var baseUrl = (settings.RemoteOcrBaseUrl ?? settings.ApiBaseUrl ?? string.Empty).TrimEnd('/');
            var apiKey  = settings.RemoteOcrApiKey ?? settings.Auth?.ApiKey ?? string.Empty;

            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("OCR base URL is empty.");

            _client = new OcrClient(baseUrl, apiKey);
        }

        public Task<ExtractResponse> ExtractAsync(Stream image, CancellationToken ct = default) =>
            _client.ExtractAsync(image, ct);

        public async Task<ExtractResponse> ExtractAsync(string path, CancellationToken ct = default)
        {
            await using var fs = File.OpenRead(path);
            return await _client.ExtractAsync(fs, ct).ConfigureAwait(false);
        }
    }
}
