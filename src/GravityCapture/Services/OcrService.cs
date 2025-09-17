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

                // tessdata is copied next to the exe (Assets/tessdata -> tessdata)
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var tessPath = Path.Combine(baseDir, "tessdata");
                if (!Directory.Exists(tessPath))
                    throw new DirectoryNotFoundException($"tessdata folder not found at: {tessPath}");

                _engine = new TesseractEngine(tessPath, "eng", EngineMode.LstmOnly);
                _engine.SetVariable("user_defined_dpi", "300"); // helps on game UIs
                _engine.DefaultPageSegMode = PageSegMode.Auto;  // good general default
            }
        }

        /// <summary>Run OCR on a bitmap and return trimmed, non-empty lines.</summary>
        public static IList<string> ReadLines(Bitmap bmp)
        {
            EnsureEngine();

            // Encode to PNG in memory, then let Tesseract/Leptonica load it
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            var bytes = ms.ToArray();

            using var pix = Pix.LoadFromMemory(bytes);
            using var page = _engine!.Process(pix);

            var text = page.GetText() ?? string.Empty;
            var lines = new List<string>();
            using var sr = new StringReader(text);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                line = line.Trim();
                if (!string.IsNullOrEmpty(line))
                    lines.Add(line);
            }
            return lines;
        }
    }
}
