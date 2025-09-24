using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Keeps old call sites working; delegates to OcrClient.
    /// </summary>
    public class OcrIngestor
    {
        private readonly AppSettings _settings;
        private readonly OcrClient   _client;

        public OcrIngestor() : this(AppSettings.Load()) { }

        public OcrIngestor(AppSettings settings)
        {
            _settings = settings ?? new AppSettings();
            var baseUrl = string.IsNullOrWhiteSpace(_settings.RemoteOcrBaseUrl)
                ? _settings.ApiBaseUrl
                : _settings.RemoteOcrBaseUrl;

            _client = new OcrClient(baseUrl, _settings.RemoteOcrApiKey);
        }

        public Task<ExtractResponse> ScanAndPostAsync(string imagePath) =>
            ScanAndPostAsync(imagePath, CancellationToken.None);

        public async Task<ExtractResponse> ScanAndPostAsync(string imagePath, CancellationToken ct)
        {
            using var fs = File.OpenRead(imagePath);
            return await ScanAndPostAsync(fs, ct);
        }

        public Task<ExtractResponse> ScanAndPostAsync(Stream stream) =>
            ScanAndPostAsync(stream, CancellationToken.None);

        public async Task<ExtractResponse> ScanAndPostAsync(Stream stream, CancellationToken ct)
        {
            return await _client.ExtractAsync(stream, ct);
        }

        public Task<ExtractResponse> ExtractAsync(Stream stream, CancellationToken ct = default) =>
            _client.ExtractAsync(stream, ct);

        public async Task<ExtractResponse> ExtractAsync(string path, CancellationToken ct = default)
        {
            using var fs = File.OpenRead(path);
            return await _client.ExtractAsync(fs, ct);
        }
    }

    // Keep ExtractResponse here; OcrLine already exists elsewhere in Services.
    public record ExtractResponse(string engine, double conf, List<OcrLine> lines);
}
