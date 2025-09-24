#nullable enable
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Legacy compatibility for older call sites. Provides:
    /// - ScanAndPostAsync(IntPtr hwnd, AppSettings, server, tribe, statusCb)
    /// - ScanAndPostAsync(Stream|string|byte[], ...)
    /// For remote OCR it uses RemoteOcrService. Parsed lines are posted via LogIngestClient.
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
            using var bmp = settings.UseCrop
                ? ScreenCapture.CaptureCropNormalized(hwnd, settings.CropX, settings.CropY, settings.CropW, settings.CropH)
                : ScreenCapture.Capture(hwnd);

            await status("Captured.");

            using var ms = new MemoryStream();
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

                var (okParse, evt, _err) = LogLineParser.TryParse(text, server, tribe);
                if (!okParse || evt is null) continue;

                var (ok, _error) = await LogIngestClient.PostEventAsync(evt);
                if (ok) posted++;
            }

            await status($"OCR lines: {seen}, posted: {posted}.");
        }

        // Stream/path/bytes shims for test utilities.
        public async Task<ExtractResponse> ScanAndPostAsync(
            Stream stream,
            string _apiKey,
            string _channelId,
            string _tribeName,
            CancellationToken ct)
        {
            var settings = AppSettings.Load();
            var remote = new RemoteOcrService(settings);
            return await remote.ExtractAsync(stream, ct);
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
            return await remote.ExtractAsync(path, ct);
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
