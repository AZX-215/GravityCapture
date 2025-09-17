using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;          // needs System.Drawing.Common
using System.IO;
using System.Runtime.InteropServices;
using Tesseract;

// Avoid clash with Tesseract.ImageFormat
using SdImageFormat = System.Drawing.Imaging.ImageFormat;

namespace GravityCapture.Services
{
    public static class OcrService
    {
        private static readonly object _lock = new();
        private static TesseractEngine? _engine;
        private static string? _tessdataPath;

        // Set GC_DEBUG_OCR=1 to dump preprocessed frames
        private static readonly bool DebugDump =
            string.Equals(Environment.GetEnvironmentVariable("GC_DEBUG_OCR"), "1",
                          StringComparison.OrdinalIgnoreCase);
        private static int _debugIdx = 0;

        public static List<string> ReadLines(Bitmap source)
        {
            EnsureEngine();

            using var work = PreprocessForLogs(source);
            if (DebugDump) Dump(work);

            using var pix = BitmapToPix(work);
            using var page = _engine!.Process(pix, PageSegMode.SingleColumn); // column of text

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

        // ---------------- internals ----------------

        private static void EnsureEngine()
        {
            if (_engine != null) return;

            lock (_lock)
            {
                if (_engine != null) return;

                var baseDir = AppContext.BaseDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var candidates = new[]
                {
                    Path.Combine(baseDir, "tessdata"),
                    Path.Combine(baseDir, "Assets", "tessdata"),
                };

                foreach (var p in candidates)
                {
                    if (Directory.Exists(p)) { _tessdataPath = p; break; }
                }

                if (_tessdataPath == null)
                    throw new DirectoryNotFoundException(
                        $"tessdata folder not found. Checked:\n - {string.Join("\n - ", candidates)}");

                _engine = new TesseractEngine(_tessdataPath, "eng", EngineMode.LstmOnly);
                _engine.DefaultPageSegMode = PageSegMode.SingleColumn;

                // Bias toward ASCII punctuation and numerics used in logs
                _engine.SetVariable("tessedit_char_whitelist",
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ,:+-()[]'!./");
                _engine.SetVariable("preserve_interword_spaces", "1");
            }
        }

        /// <summary>
        /// HDR-friendly preprocessing:
        ///  - upscale a bit
        ///  - convert to grayscale with strong RED weighting (keeps red kill lines bright)
        ///  - mild sharpen
        /// </summary>
        private static Bitmap PreprocessForLogs(Bitmap input)
        {
            const double scale = 1.40; // light upscale helps OCR
            var w = Math.Max(1, (int)Math.Round(input.Width * scale));
            var h = Math.Max(1, (int)Math.Round(input.Height * scale));

            // 1) upscale
            var up = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(up))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode  = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(input, new Rectangle(0, 0, w, h));
            }

            // 2) red-weighted grayscale: 0.60R + 0.30G + 0.10B
            var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            RedWeightedGrayscale(up, gray);
            up.Dispose();

            // 3) gentle sharpen kernel
            var sharp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            Convolve3x3(gray, sharp, new float[,]
            {
                { 0, -1,  0 },
                { -1, 5, -1 },
                { 0, -1,  0 }
            }, 1f, 0f);
            gray.Dispose();

            return sharp;
        }

        private static Pix BitmapToPix(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, SdImageFormat.Png);
            return Pix.LoadFromMemory(ms.ToArray());
        }

        private static void Dump(Bitmap bmp)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GravityCapture", "ocr-dump");

                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"frame-{_debugIdx++:0000}.png");
                bmp.Save(path, SdImageFormat.Png);
            }
            catch { /* ignore */ }
        }

        // ---------- image helpers ----------

        private static void RedWeightedGrayscale(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int strideS = sData.Stride;
                int strideD = dData.Stride;

                unsafe
                {
                    byte* sPtr = (byte*)sData.Scan0;
                    byte* dPtr = (byte*)dData.Scan0;

                    for (int y = 0; y < src.Height; y++)
                    {
                        byte* sp = sPtr + y * strideS;
                        byte* dp = dPtr + y * strideD;

                        for (int x = 0; x < src.Width; x++)
                        {
                            // 24bpp: B G R
                            byte b = sp[0], g = sp[1], r = sp[2];

                            // Stronger emphasis on red channel (HDR red text)
                            int val = (int)Math.Round(0.10 * b + 0.30 * g + 0.60 * r);
                            if (val < 0) val = 0; if (val > 255) val = 255;
                            byte v = (byte)val;

                            dp[0] = v; dp[1] = v; dp[2] = v;

                            sp += 3;
                            dp += 3;
                        }
                    }
                }
            }
            finally
            {
                src.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }

        /// <summary>
        /// Simple 3x3 convolution for sharpening.
        /// </summary>
        private static void Convolve3x3(Bitmap src, Bitmap dst, float[,] k, float factor, float bias)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = src.Width, h = src.Height;
                int ss = sData.Stride, ds = dData.Stride;

                unsafe
                {
                    byte* sBase = (byte*)sData.Scan0;
                    byte* dBase = (byte*)dData.Scan0;

                    for (int y = 1; y < h - 1; y++)
                    {
                        for (int x = 1; x < w - 1; x++)
                        {
                            float sum = 0f;

                            // Neighborhood indices in bytes
                            int i00 = (y - 1) * ss + (x - 1) * 3;
                            int i01 = (y - 1) * ss + (x    ) * 3;
                            int i02 = (y - 1) * ss + (x + 1) * 3;

                            int i10 = (y    ) * ss + (x - 1) * 3;
                            int i11 = (y    ) * ss + (x    ) * 3;
                            int i12 = (y    ) * ss + (x + 1) * 3;

                            int i20 = (y + 1) * ss + (x - 1) * 3;
                            int i21 = (y + 1) * ss + (x    ) * 3;
                            int i22 = (y + 1) * ss + (x + 1) * 3;

                            // grayscale, any channel
                            sum += sBase[i00] * k[0,0] + sBase[i01] * k[0,1] + sBase[i02] * k[0,2];
                            sum += sBase[i10] * k[1,0] + sBase[i11] * k[1,1] + sBase[i12] * k[1,2];
                            sum += sBase[i20] * k[2,0] + sBase[i21] * k[2,1] + sBase[i22] * k[2,2];

                            int v = (int)Math.Round(sum * factor + bias);
                            if (v < 0) v = 0; if (v > 255) v = 255;

                            int di = y * ds + x * 3;
                            dBase[di + 0] = (byte)v;
                            dBase[di + 1] = (byte)v;
                            dBase[di + 2] = (byte)v;
                        }
                    }
                }

                // Copy borders without change
                using var g = Graphics.FromImage(dst);
                g.DrawImage(src, 0, 0);
            }
            finally
            {
                src.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }
    }
}
