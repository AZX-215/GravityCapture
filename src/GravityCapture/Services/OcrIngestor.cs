using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Thin wrapper that preserves the old public surface so callers like
    /// MainWindow and Program keep compiling. Internally calls OcrClient.
    /// </summary>
    public class OcrIngestor
    {
        private readonly AppSettings _settings;
        private readonly OcrClient   _client;

        public OcrIngestor() : this(AppSettings.Load()) { }

        public OcrIngestor(AppSettings settings)
        {
            _settings = settings ?? new AppSettings();
            // Prefer new Remote OCR settings; fall back to legacy ApiBaseUrl if set.
            var baseUrl = string.IsNullOrWhiteSpace(_settings.RemoteOcrBaseUrl)
                ? _settings.ApiBaseUrl
                : _settings.RemoteOcrBaseUrl;

            _client = new OcrClient(baseUrl, _settings.RemoteOcrApiKey);
        }

        // ===== Back-compat overloads used throughout the app =====

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
            // Stage app only needs OCR; “post”/ingest is handled elsewhere.
            return await _client.ExtractAsync(stream, ct);
        }

        // Some callers use ExtractAsync naming
        public Task<ExtractResponse> ExtractAsync(Stream stream, CancellationToken ct = default) =>
            _client.ExtractAsync(stream, ct);

        public async Task<ExtractResponse> ExtractAsync(string path, CancellationToken ct = default)
        {
            using var fs = File.OpenRead(path);
            return await _client.ExtractAsync(fs, ct);
        }
    }

    /// <summary>Shared models so the app compiles even if callers referenced them.</summary>
    public record OcrLine(string text, double conf, int[] bbox);
    public record ExtractResponse(string engine, double conf, System.Collections.Generic.List<OcrLine> lines);
}
