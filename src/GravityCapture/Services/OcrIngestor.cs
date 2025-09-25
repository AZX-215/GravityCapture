#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Single source of truth for OCR ingest. Contains the recent-line deduper
    /// and all ScanAndPostAsync overloads.
    /// </summary>
    public partial class OcrIngestor
    {
        // ---- recent-line deduper ----
        private readonly object _gate = new();
        private readonly LinkedList<(string text, DateTime when)> _recent = new();
        private const int MaxRecent = 512;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

        /// <summary>Returns true if the line was not seen recently, then registers it.</summary>
        public bool TryRegisterLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var now = DateTime.UtcNow;
            lock (_gate)
            {
                while (_recent.Count > 0 && now - _recent.First!.Value.when > Window)
                    _recent.RemoveFirst();

                foreach (var (t, _) in _recent)
                    if (string.Equals(t, text, StringComparison.Ordinal))
                        return false;

                _recent.AddLast((text, now));
                if (_recent.Count > MaxRecent) _recent.RemoveFirst();
                return true;
            }
        }

        public void ClearRecent() { lock (_gate) _recent.Clear(); }

        // ---- primary pipeline (capture -> remote OCR -> tally) ----
        // Posting to your backend is intentionally omitted here to avoid
        // compile-time mismatches with LogIngestClient. Add back later.
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

                int seen = 0, unique = 0;
                foreach (var line in res.Lines)
                {
                    var text = (line.Text ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    seen++;
                    if (TryRegisterLine(text)) unique++;
                    // TODO: call LogLineParser + LogIngestClient when final API is settled
                }

                await status($"OCR lines: {seen}, unique (not recently seen): {unique}.");
            }
            finally
            {
                bmp.Dispose();
            }
        }

        // ---- legacy/test overloads (remote OCR only) ----
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
            await using var fs = File.OpenRead(path);
            return await remote.ExtractAsync(fs, ct).ConfigureAwait(false);
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
