#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models; // ExtractResponse

namespace GravityCapture.Services
{
    /// <summary>
    /// Legacy compatibility shims for older call sites that still invoke
    /// ScanAndPostAsync(... apiKey, channelId, tribeName, ...).
    /// These forward to the remote OCR path.
    /// </summary>
    public partial class OcrIngestor
    {
        private readonly RemoteOcrService _client;

        public OcrIngestor(RemoteOcrService client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// Legacy signature: forwards the provided image stream to the remote OCR API.
        /// Unused parameters are accepted for compatibility.
        /// </summary>
        public Task<ExtractResponse> ScanAndPostAsync(
            Stream stream,
            string _apiKey,
            string _channelId,
            string _tribeName,
            CancellationToken ct)
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));
            return _client.ExtractAsync(stream, ct);
        }

        /// <summary>
        /// Legacy signature: convenience overload for file paths.
        /// </summary>
        public async Task<ExtractResponse> ScanAndPostAsync(
            string path,
            string _apiKey,
            string _channelId,
            string _tribeName,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            await using var fs = File.OpenRead(path);
            return await _client.ExtractAsync(fs, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Legacy signature: convenience overload for byte arrays.
        /// </summary>
        public Task<ExtractResponse> ScanAndPostAsync(
            byte[] bytes,
            string _apiKey,
            string _channelId,
            string _tribeName,
            CancellationToken ct)
        {
            if (bytes is null) throw new ArgumentNullException(nameof(bytes));
            var ms = new MemoryStream(bytes, writable: false);
            // Intentionally not disposing ms here since the callee reads from the stream asynchronously.
            return _client.ExtractAsync(ms, ct);
        }
    }
}
