using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Tesseract;

namespace GravityCapture.Services
{
    public static class OcrService
    {
        private static readonly object _lock = new();
        private static TesseractEngine? _engine;
        private static string? _tessdataPath;

        /// <summary>
        /// OCR the given bitmap and return non-empty, trimmed lines, in order.
        /// </summary>
        public static List<string> ReadLines(Bitmap source)
        {
            EnsureEngine();

            using var work = PreprocessForLogs(source);
            using var px = BitmapToPix(work);

            using var page = _engine!.Process(px, PageSegMode.Auto);
            var text = page.GetText() ?? string.Empty;

            var lines = new List<string>();
            using var sr = new StringReader(text);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                line = line.Trim();
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }
            return lines;
        }

        // ---------------- internal helpers ----------------

        /// <summary>
        /// Initialize tesseract engine once, locating tessdata near the executable.
        /// Looks in:
        ///   1) {AppContext.BaseDirectory}\tessdata
        ///   2) {AppContext.BaseDirectory}\Assets\tessdata
        /// </summary>
        private static void EnsureEngine()
        {
            if (_engine != null) return;

            lock (_lock)
            {
                if (_engine != null) return;

                var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var candidates = new[]
                {
                    Path.Combine(baseDir, "tessdata"),
                    Path.Combine(baseDir, "Assets", "tessdata"),
                };

                foreach (var p in candidates)
                {
                    if (Directory.Exists(p))
                    {
                        _tessdataPath = p;
                        break;
                    }
                }

                if (_tessdataPath == null)
                    throw new DirectoryNotFoundException(
                        $"tessdata folder not found. Checked:\n - {string.Join("\n - ", candidates)}");

                // Expect eng.traineddata inside _tessdataPath
                _engine = new TesseractEngine(_tessdataPath, "eng", EngineMode.LstmOnly);
                // Log text is monospaced-ish; forcing PSM SingleBlock often helps
                _engine.DefaultPageSegMode = PageSegMode.SingleBlock;
            }
        }

        /// <summary>
        /// Lightweight pre-processing that helps with HDR: scale up a bit,
        /// convert to 24bpp, boost contrast slightly, and gray.
        /// </summary>
        private static Bitmap PreprocessForLogs(Bitmap input)
        {
            // 1) upscale modestly to help OCR on small UI text
            double scale = 1.35;
            var w = Math.Max(1, (int)Math.Round(input.Width * scale));
            var h = Math.Max(1, (int)Math.Round(input.Height * scale));

            var scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(input, new Rectangle(0, 0, w, h));
            }

            // 2) apply a mild contrast boost via ColorMatrix
            //    (kept simple; avoids unsafe/pixel loops)
            float c = 1.20f; // contrast
            float t = 0.5f * (1f - c); // translate
            var cm = new ColorMatrix(new float[][]
            {
                new float[] { c, 0, 0, 0, 0 },
                new float[] { 0, c, 0, 0, 0 },
                new float[] { 0, 0, c, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { t, t, t, 0, 1 }
            });

            var contrasted = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(contrasted))
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(scaled, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }
            scaled.Dispose();

            // 3) quick grayscale (ColorMatrix is already close; ensure neutral)
            var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(gray))
            {
                var grayMatrix = new ColorMatrix(new float[][]
                {
                    new float[] {.299f, .299f, .299f, 0, 0},
                    new float[] {.587f, .587f, .587f, 0, 0},
                    new float[] {.114f, .114f, .114f, 0, 0},
                    new float[] {0,     0,     0,    1, 0},
                    new float[] {0,     0,     0,    0, 1}
                });
                using var attr = new ImageAttributes();
                attr.SetColorMatrix(grayMatrix);
                g.DrawImage(contrasted, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, attr);
            }
            contrasted.Dispose();

            return gray;
        }

        /// <summary>
        /// Convert a Bitmap to Tesseract Pix by saving to memory (no PixConverter dependency).
        /// </summary>
        private static Pix BitmapToPix(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.System.Drawing.Imaging.ImageFormat.Png);
            return Pix.LoadFromMemory(ms.ToArray());
        }
    }
}


