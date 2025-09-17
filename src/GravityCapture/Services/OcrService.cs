using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Tesseract;

namespace GravityCapture.Services
{
    public static class OcrService
    {
        private static readonly object _lock = new();
        private static TesseractEngine? _engine;

        private static void EnsureEngine()
        {
            if (_engine != null) return;
            lock (_lock)
            {
                if (_engine != null) return;

                // Look for tessdata next to the app
                // (we copy Assets/tessdata/* to output at build time)
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var tessPath = Path.Combine(baseDir, "tessdata");
                if (!Directory.Exists(tessPath))
                    throw new DirectoryNotFoundException($"tessdata folder not found at: {tessPath}");

                // "eng" is enough for ASA logs. EngineMode.LstmOnly is fast & accurate.
                _engine = new TesseractEngine(tessPath, "eng", EngineMode.LstmOnly);
                // Logs are light text on dark background → improve contrast by telling Tesseract it's "single block"
                _engine.SetVariable("user_defined_dpi", "300");
            }
        }

        /// <summary>Run OCR and return non-empty lines (trimmed).</summary>
        public static IList<string> ReadLines(Bitmap bmp)
        {
            EnsureEngine();
            using var pix = PixConverter.ToPix(bmp);
            using var page = _engine!.Process(pix);
            var text = page.GetText() ?? string.Empty;

            var lines = new List<string>();
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (!string.IsNullOrEmpty(line))
                    lines.Add(line);
            }
            return lines;
        }
    }
}
