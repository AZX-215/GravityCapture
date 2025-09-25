#nullable enable
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Legacy compatibility + remote OCR pipeline glue. Single source of truth.
    /// </summary>
    public partial class OcrIngestor
    {
        public async Task ScanAndPostAsync(
            IntPtr hwnd,
            AppSettings settings,
            string server,
            string tribe,
            Func<string, Task> status)
        {
            var bmp = settings.UseCrop
                ? ScreenCapture.CaptureCropNormalized(hwnd, settings.CropX, settings.CropY, settings.CropW, settings.CropH)
                : ScreenCapture.Capture(hwnd);

            await status("Captured.");

            try
            {
                await using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                var remote = new RemoteOcrService(settings);
                var res = await remote.ExtractAsync(ms, CancellationToken.None);

                int posted = 0, seen = 0;
                foreach (var line in res.Lines)
                {
                    var text = (line.Text ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    seen++;
                    if (!TryRegisterLine(text)) continue;

                    var (okParse, evt, _) = LogLineParser.TryParse(text, server, tribe);
                    if (!okParse || evt is null) continue;

                    var (ok, _) = await LogIngestClient.PostEventAsync(evt);
                    if (ok) posted++;
                }

                await status($"OCR lines: {seen}, posted: {posted}.");
            }
            finally
            {
                bmp.Dispose();
            }
        }

        // ---- Legacy overloads used by tools/tests ----

        public Task<ExtractResponse> ScanAndPostAsync(
            Stream stream,
            string _apiKey,
            string _channelId,
            string _tribeName,
            CancellationToken ct)
        {
            var settings = AppSettings.Load();
            var remote = new RemoteOcrService(settings);
            return remote.ExtractAsync(stream, ct);
        }

        public async Task<ExtractResponse> ScanAndPostAsync(
            string path,
            string _apiKey,
            string _channelId,
            string _tribeName,
            CancellationToken ct)
        {
            var settings = AppSettings.Load();
            var remote = new RemoteOcrService(settings);
            return await remote.ExtractAsync(path, ct).ConfigureAwait(false);
        }

        public Task<ExtractResponse> ScanAndPostAsync(
            byte[] bytes,
            string _apiKey,
            string _channelId,
            string _tribeName,
            CancellationToken ct)
        {
            var ms = new MemoryStream(bytes, writable: false);
            return ScanAndPostAsync(ms, _apiKey, _channelId, _tribeName, ct);
        }
    }
}
