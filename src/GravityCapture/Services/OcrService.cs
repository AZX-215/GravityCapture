using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;              // System.Drawing.Common
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
            using var page = _engine!.Process(pix, PageSegMode.SingleColumn);

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
            const double scale = 1.40;
            int w = Math.Max(1, (int)Math.Round(input.Width * scale));
            int h = Math.Max(1, (int)Math.Round(input.Height * scale));

            // 1) upscale
            var up = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(up))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(input, new Rectangle(0, 0, w, h));
            }

            // 2) red-weighted grayscale
            var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            RedWeightedGrayscaleSafe(up, gray);
            up.Dispose();

            // 3) mild sharpen
            var sharp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            Convolve3x3Safe(gray, sharp, new float[,]
            {
                { 0, -1,  0 },
                { -1, 5, -1 },
                { 0, -1,  0 }
            });
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

        // ---------- SAFE image helpers (no 'unsafe' blocks) ----------

        private static void RedWeightedGrayscaleSafe(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);

            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int strideS = sData.Stride;
                int strideD = dData.Stride;
                int bytesS = Math.Abs(strideS) * src.Height;
                int bytesD = Math.Abs(strideD) * dst.Height;

                var sbuf = new byte[bytesS];
                var dbuf = new byte[bytesD];

                Marshal.Copy(sData.Scan0, sbuf, 0, bytesS);

                for (int y = 0; y < src.Height; y++)
                {
                    int sRow = y * strideS;
                    int dRow = y * strideD;
                    for (int x = 0; x < src.Width; x++)
                    {
                        int si = sRow + x * 3;
                        byte b = sbuf[si + 0];
                        byte g = sbuf[si + 1];
                        byte r = sbuf[si + 2];

                        int val = (int)Math.Round(0.10 * b + 0.30 * g + 0.60 * r);
                        if (val < 0) val = 0; if (val > 255) val = 255;
                        byte v = (byte)val;

                        int di = dRow + x * 3;
                        dbuf[di + 0] = v;
                        dbuf[di + 1] = v;
                        dbuf[di + 2] = v;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, bytesD);
            }
            finally
            {
                src.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }

        private static void Convolve3x3Safe(Bitmap src, Bitmap dst, float[,] k)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);

            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = src.Width, h = src.Height;
                int ss = sData.Stride, ds = dData.Stride;
                int bytesS = Math.Abs(ss) * h;
                int bytesD = Math.Abs(ds) * h;

                var sbuf = new byte[bytesS];
                var dbuf = new byte[bytesD];

                Marshal.Copy(sData.Scan0, sbuf, 0, bytesS);

                // copy borders directly
                Buffer.BlockCopy(sbuf, 0, dbuf, 0, ss);                              // top row
                Buffer.BlockCopy(sbuf, (h - 1) * ss, dbuf, (h - 1) * ds, ss);        // bottom row

                for (int y = 1; y < h - 1; y++)
                {
                    // left border pixel (copy)
                    Buffer.BlockCopy(sbuf, y * ss, dbuf, y * ds, 3);

                    for (int x = 1; x < w - 1; x++)
                    {
                        float sum =
                            sbuf[(y - 1) * ss + (x - 1) * 3] * k[0, 0] +
                            sbuf[(y - 1) * ss + (x    ) * 3] * k[0, 1] +
                            sbuf[(y - 1) * ss + (x + 1) * 3] * k[0, 2] +
                            sbuf[(y    ) * ss + (x - 1) * 3] * k[1, 0] +
                            sbuf[(y    ) * ss + (x    ) * 3] * k[1, 1] +
                            sbuf[(y    ) * ss + (x + 1) * 3] * k[1, 2] +
                            sbuf[(y + 1) * ss + (x - 1) * 3] * k[2, 0] +
                            sbuf[(y + 1) * ss + (x    ) * 3] * k[2, 1] +
                            sbuf[(y + 1) * ss + (x + 1) * 3] * k[2, 2];

                        int v = (int)Math.Round(sum);
                        if (v < 0) v = 0; if (v > 255) v = 255;

                        int di = y * ds + x * 3;
                        dbuf[di + 0] = (byte)v;
                        dbuf[di + 1] = (byte)v;
                        dbuf[di + 2] = (byte)v;
                    }

                    // right border pixel (copy)
                    Buffer.BlockCopy(sbuf, y * ss + (w - 1) * 3, dbuf, y * ds + (w - 1) * 3, 3);
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, bytesD);
            }
            finally
            {
                src.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }
    }
}
