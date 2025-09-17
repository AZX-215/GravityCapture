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

        // Set GC_OCR_TONEMAP=0 to disable the auto tone-mapping step (for debugging or edge cases)
        private static readonly bool ToneMapEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_TONEMAP"), "0",
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

                // Bias Tesseract toward characters/log punctuation we expect in tribe logs
                _engine.SetVariable("tessedit_char_whitelist",
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ,:+-()[]'!./");
                _engine.SetVariable("preserve_interword_spaces", "1");
            }
        }

        /// <summary>
        /// HDR/SDR friendly preprocessing:
        ///  - upscale a bit for OCR stability
        ///  - (optional) adaptive tone-map (compress blown highlights + gentle gamma)
        ///  - Rec.709 luminance grayscale (preserves cyan/green/yellow UI text visibility)
        ///  - Otsu binarization for crisp text
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

            // 2) Adaptive tone-map (helps HDR captures that look washed out after OS compositing)
            if (ToneMapEnabled)
            {
                ToneMapAutoInPlace(up);
            }

            // 3) Convert to Rec.709 luma grayscale (works better than red-weighted for cyan/green/yellow)
            var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            ToLuma709Safe(up, gray);
            up.Dispose();

            // 4) Otsu binarization to get crisp text
            var bin = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            OtsuBinarizeSafe(gray, bin);
            gray.Dispose();

            // 5) Mild sharpen (same kernel as before)
            var sharp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            Convolve3x3Safe(bin, sharp, new float[,]
            {
                { 0, -1,  0 },
                { -1, 5, -1 },
                { 0, -1,  0 }
            });
            bin.Dispose();

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

        /// <summary>
        /// Compress highlights if the 95th percentile is too bright, then apply a gentle gamma to darken washed-out UIs.
        /// This adapts automatically; on good SDR or already-correct SDR captures, the computed scale/gamma are ~1.0.
        /// </summary>
        private static void ToneMapAutoInPlace(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                var hist = new int[256];

                // Build histogram using max(R,G,B) to catch highlight rail
                var bytes = Math.Abs(stride) * h;
                var buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);

                int idx = 0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 3;
                        byte b = buf[i + 0];
                        byte g = buf[i + 1];
                        byte r = buf[i + 2];
                        byte m = (byte)Math.Max(r, Math.Max(g, b));
                        hist[m]++;
                    }
                }

                int total = w * h;
                int targetCount = (int)(total * 0.95);
                int acc = 0, p95 = 255;
                for (int i = 0; i < 256; i++)
                {
                    acc += hist[i];
                    if (acc >= targetCount) { p95 = i; break; }
                }

                double mean = 0;
                for (int i = 0; i < 256; i++) mean += i * (double)hist[i];
                mean /= Math.Max(1, total);

                // If highlights are near the rails, pull them down to ~220 and add a small gamma (>1 darkens)
                double scale = p95 > 235 ? 220.0 / p95 : 1.0;
                double gamma = mean > 160 ? 1.35 : 1.0;

                if (Math.Abs(scale - 1.0) > 0.01 || Math.Abs(gamma - 1.0) > 0.01)
                {
                    var lut = new byte[256];
                    for (int i = 0; i < 256; i++)
                    {
                        double v = i / 255.0;
                        v = Math.Pow(Math.Min(1.0, v * scale), gamma);
                        lut[i] = (byte)(v * 255.0 + 0.5);
                    }

                    for (int y = 0; y < h; y++)
                    {
                        int row = y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            int i = row + x * 3;
                            buf[i + 0] = lut[buf[i + 0]];
                            buf[i + 1] = lut[buf[i + 1]];
                            buf[i + 2] = lut[buf[i + 2]];
                        }
                    }

                    Marshal.Copy(buf, 0, data.Scan0, bytes);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>
        /// RGB → Rec.709 luma grayscale (writes 24bppRgb with R=G=B=Y).
        /// </summary>
        private static void ToLuma709Safe(Bitmap src, Bitmap dst)
        {
            int w = src.Width, h = src.Height;
            var rect = new Rectangle(0, 0, w, h);

            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int ss = sData.Stride, ds = dData.Stride;
                int bytesS = Math.Abs(ss) * h;
                int bytesD = Math.Abs(ds) * h;

                var sbuf = new byte[bytesS];
                var dbuf = new byte[bytesD];

                Marshal.Copy(sData.Scan0, sbuf, 0, bytesS);

                for (int y = 0; y < h; y++)
                {
                    int sRow = y * ss;
                    int dRow = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        int si = sRow + x * 3;
                        byte b = sbuf[si + 0];
                        byte g = sbuf[si + 1];
                        byte r = sbuf[si + 2];

                        int yv = (int)Math.Round(0.0722 * b + 0.7152 * g + 0.2126 * r);
                        if (yv < 0) yv = 0; if (yv > 255) yv = 255;
                        byte yy = (byte)yv;

                        int di = dRow + x * 3;
                        dbuf[di + 0] = yy;
                        dbuf[di + 1] = yy;
                        dbuf[di + 2] = yy;
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

        /// <summary>
        /// Global Otsu threshold on 24bppRgb grayscale; writes 0/255 to dst.
        /// </summary>
        private static void OtsuBinarizeSafe(Bitmap srcGray, Bitmap dst)
        {
            int w = srcGray.Width, h = srcGray.Height;
            var rect = new Rectangle(0, 0, w, h);

            var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int ss = sData.Stride, ds = dData.Stride;
                int bytesS = Math.Abs(ss) * h;
                int bytesD = Math.Abs(ds) * h;

                var sbuf = new byte[bytesS];
                var dbuf = new byte[bytesD];

                Marshal.Copy(sData.Scan0, sbuf, 0, bytesS);

                // Histogram (single channel; R=G=B)
                var hist = new int[256];
                for (int y = 0; y < h; y++)
                {
                    int row = y * ss;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = sbuf[row + x * 3];
                        hist[v]++;
                    }
                }

                int total = w * h;
                double sum = 0;
                for (int t = 0; t < 256; t++) sum += t * hist[t];

                int wB = 0;
                double sumB = 0, varMax = 0;
                int threshold = 128;

                for (int t = 0; t < 256; t++)
                {
                    wB += hist[t];
                    if (wB == 0) continue;
                    int wF = total - wB;
                    if (wF == 0) break;

                    sumB += t * hist[t];
                    double mB = sumB / wB;
                    double mF = (sum - sumB) / wF;
                    double varBetween = wB * wF * (mB - mF) * (mB - mF);

                    if (varBetween > varMax) { varMax = varBetween; threshold = t; }
                }

                // Apply threshold
                for (int y = 0; y < h; y++)
                {
                    int sRow = y * ss;
                    int dRow = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = sbuf[sRow + x * 3];
                        byte o = (byte)(v >= threshold ? 255 : 0);
                        int di = dRow + x * 3;
                        dbuf[di + 0] = o; dbuf[di + 1] = o; dbuf[di + 2] = o;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, bytesD);
            }
            finally
            {
                srcGray.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }

        // Existing general-purpose 3x3 convolution (kept from your original implementation)
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

                // copy top/bottom rows unchanged
                Buffer.BlockCopy(sbuf, 0, dbuf, 0, ss);
                Buffer.BlockCopy(sbuf, (h - 1) * ss, dbuf, (h - 1) * ds, ss);

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

        // --- (Optional legacy helper kept for reference; not used in the new pipeline) ---
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
    }
}
