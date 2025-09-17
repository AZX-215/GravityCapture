using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Keeps a rolling set of recently-seen OCR lines and posts newly-seen ones.
    /// Honors red-only and per-category filters from AppSettings.
    /// </summary>
    public sealed class OcrIngestor
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();
        private readonly int _capacity;

        private bool _busy;

        public OcrIngestor(int capacity = 300) => _capacity = Math.Max(50, capacity);

        public async Task ScanAndPostAsync(
            IntPtr hwnd,
            AppSettings settings,
            string server,
            string tribe,
            Func<string, Task> statusSink)
        {
            if (_busy) return; // reentrancy guard
            _busy = true;
            try
            {
                if (!settings.UseCrop)
                {
                    await statusSink("Auto OCR is on but no crop is saved.");
                    return;
                }

                using Bitmap bmp = ScreenCapture.CaptureCropNormalized(
                    hwnd, settings.CropX, settings.CropY, settings.CropW, settings.CropH);

                var lines = OcrService.ReadLines(bmp);

                int posted = 0, parsed = 0;
                foreach (var raw in lines)
                {
                    var norm = Normalize(raw);
                    if (AlreadySeen(norm)) continue;

                    var (ok, evt, err) = LogLineParser.TryParse(raw, server, tribe);
                    if (!ok || evt == null) continue;

                    parsed++;

                    // Red-only filter
                    if (settings.PostOnlyCritical &&
                        !string.Equals(evt.severity, "CRITICAL", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Per-category filters
                    if (!IsCategoryAllowed(evt.category, settings))
                        continue;

                    var (postedOk, perr) = await LogIngestClient.PostEventAsync(evt);
                    if (postedOk) posted++;
                    else await statusSink($"Post failed: {perr ?? "unknown"}");
                }

                if (posted > 0) await statusSink($"Auto OCR: posted {posted} of {parsed} parsed line(s).");
            }
            finally
            {
                _busy = false;
            }
        }

        private static bool IsCategoryAllowed(string? category, AppSettings settings)
        {
            if (string.IsNullOrWhiteSpace(category)) return true;

            switch (category.Trim().ToUpperInvariant())
            {
                case "TAME_DEATH":
                    return settings.FilterTameDeath;

                case "STRUCTURE_DESTROYED":
                case "STRUCTURE_DAMAGE":
                    return settings.FilterStructureDestroyed;

                case "TRIBE_MATE_DEATH":
                case "TRIBE_MEMBER_DEATH":
                case "TRIBE_DEATH":
                    return settings.FilterTribeMateDeath;

                default:
                    // Allow unknowns for now; you can tighten later as we add more categories.
                    return true;
            }
        }

        private bool AlreadySeen(string s)
        {
            if (_seen.Contains(s)) return true;
            _seen.Add(s);
            _order.Enqueue(s);
            while (_order.Count > _capacity)
            {
                var old = _order.Dequeue();
                _seen.Remove(old);
            }
            return false;
        }

        private static string Normalize(string s)
            => s.Trim().Replace("  ", " ");
    }
}

