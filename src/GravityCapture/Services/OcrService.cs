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

                var tessDir = FindTessdataDir();
                if (tessDir == null)
                {
                    // Build a helpful message listing where we looked.
                    var searched = string.Join(
                        Environment.NewLine + " - ",
                        ProbeCandidateDirs());
                    throw new DirectoryNotFoundException(
                        "tessdata/eng.traineddata not found." + Environment.NewLine +
                        "Add 'eng.traineddata' to one of these folders (and set Build Action=Content, Copy to Output=Copy if newer if inside the project):" + Environment.NewLine +
                        " - " + searched);
                }

                _engine = new TesseractEngine(tessDir, "eng", EngineMode.LstmOnly);
                _engine.SetVariable("user_defined_dpi", "300");   // helps on game UIs
                _engine.DefaultPageSegMode = PageSegMode.Auto;     // good general default
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

        // ---------- helpers ----------

        /// <summary>
        /// Return the path to the *tessdata* directory that contains eng.traineddata,
        /// or null if none found.
        /// </summary>
        private static string? FindTessdataDir()
        {
            foreach (var dir in ProbeCandidateDirs())
            {
                try
                {
                    if (Directory.Exists(dir) &&
                        File.Exists(Path.Combine(dir, "eng.traineddata")))
                    {
                        return dir; // this is the tessdata folder
                    }
                }
                catch { /* ignore permission issues, keep probing */ }
            }
            return null;
        }

        /// <summary>
        /// Yields candidate directories that might be the tessdata folder.
        /// Order matters: we prefer next-to-exe first.
        /// </summary>
        private static IEnumerable<string> ProbeCandidateDirs()
        {
            var baseDir = AppContext.BaseDirectory;

            // 1) Next to the exe (recommended): <exe>\tessdata
            yield return Path.Combine(baseDir, "tessdata");

            // 1b) Legacy/tolerated: <exe>\Assets\tessdata
            yield return Path.Combine(baseDir, "Assets", "tessdata");

            // 2) Local app data: %LOCALAPPDATA%\GravityCapture\tessdata
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GravityCapture", "tessdata");

            // 3) TESSDATA_PREFIX (either points to a folder that contains "tessdata",
            //    or to the tessdata folder itself). Try both.
            var prefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                var asTessdata = Path.Combine(prefix, "tessdata");
                yield return asTessdata;
                yield return prefix; // in case prefix already *is* the tessdata folder
            }
        }
    }
}
