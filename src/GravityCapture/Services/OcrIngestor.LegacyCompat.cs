using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GravityCapture.Services
{
    // Adds back the older 5-arg signatures used by MainWindow & friends.
    public partial class OcrIngestor
    {
        public Task<ExtractResponse> ScanAndPostAsync(
            Stream stream,
            string _apiKey,
            string _channelId,
            string _tribeName,
            CancellationToken ct)
        => _client.ExtractAsync(stream, ct);

        public async Task<ExtractResponse> ScanAndPostAsync(
            string path,
            string _apiKey,
            string _channelId,
            string _tribeName,
            CancellationToken ct)
        {
            using var fs = File.OpenRead(path);
            return await _client.ExtractAsync(fs, ct);
        }
    }
}
