using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GravityCapture.Services
{
    public sealed class OcrIngestor
    {
        private readonly OcrClient _client;

        public OcrIngestor(OcrClient client)
        {
            _client = client;
        }

        public async Task<string> ExtractFromFileAsync(string path, CancellationToken ct = default)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Image to OCR not found.", path);

            await using var fs = File.OpenRead(path);

            var result = await _client.ExtractAsync(fs, Path.GetFileName(path), ct);
            return string.Join(" ", result.Lines.Select(l => l.Text));
        }
    }
}
