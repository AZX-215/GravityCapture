using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace GravityCapture.Services
{
    public sealed class OcrIngestor
    {
        private readonly OcrClient _client;
        private readonly ILogger<OcrIngestor> _logger;

        public OcrIngestor(OcrClient client, ILogger<OcrIngestor> logger)
        {
            _client = client;
            _logger = logger;
        }

        public async Task<string> ExtractFromFileAsync(string path, CancellationToken ct = default)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Image to OCR not found.", path);

            await using var fs = File.OpenRead(path);

            // Use 'var' so we don't need the ExtractResponse type here.
            var result = await _client.ExtractAsync(fs, Path.GetFileName(path), ct);

            var text = string.Join(" ", result.Lines.Select(l => l.Text));
            _logger.LogInformation("OCR ({Engine}) conf {Conf:0.###} -> {Chars} chars",
                result.Engine, result.Conf, text.Length);

            return text;
        }
    }
}
