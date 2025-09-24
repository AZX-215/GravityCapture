using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Minimal helper the UI can new-up to call Remote OCR.
    /// No ILogger / DI to keep Stage app simple.
    /// </summary>
    public class RemoteOcrService
    {
        private readonly OcrClient _client;

        public RemoteOcrService(AppSettings settings)
        {
            var baseUrl = (settings?.RemoteOcrBaseUrl ?? "").TrimEnd('/');
            _client = new OcrClient(baseUrl, settings?.RemoteOcrApiKey ?? "");
        }

        public Task<ExtractResponse> ExtractAsync(Stream image, CancellationToken ct = default) =>
            _client.ExtractAsync(image, ct);

        public async Task<ExtractResponse> ExtractAsync(string path, CancellationToken ct = default)
        {
            using var fs = File.OpenRead(path);
            return await _client.ExtractAsync(fs, ct);
        }
    }
}
