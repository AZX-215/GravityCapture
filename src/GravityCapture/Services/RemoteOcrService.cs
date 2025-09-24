using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    public sealed class RemoteOcrService : IOcrService
    {
        private readonly OcrClient _client;

        public RemoteOcrService(AppSettings settings)
        {
            var baseUrl = settings.Api?.BaseUrl ?? "";
            var apiKey  = settings.Api?.ApiKey;
            _client = new OcrClient(baseUrl, apiKey);
        }

        public async Task<string> ExtractAsync(Bitmap bitmap, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            // Use 'var' to avoid referencing ExtractResponse directly.
            var resp = await _client.ExtractAsync(ms, "capture.png", ct);
            return string.Join(" ", resp.Lines.Select(l => l.Text));
        }
    }
}
