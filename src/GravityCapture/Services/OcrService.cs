using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;              // System.Drawing.Common
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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

        // Debug / tuning flags via environment
        private static readonly bool DebugDump =
            string.Equals(Environment.GetEnvironmentVariable("GC_DEBUG_OCR"), "1", StringComparison.OrdinalIgnoreCase);

        private static readonly bool ToneMapEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_TONEMAP"), "0", StringComparison.OrdinalIgnoreCase);

        private static readonly bool AdaptiveEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_ADAPTIVE"), "0", StringComparison.OrdinalIgnoreCase);

        private static int _debugIdx = 0;

        // ------------------------------------------------------
        // Public API
        // ------------------------------------------------------
        public static List<string> ReadLines(Bitmap source)
        {
            EnsureEngine();

            using var work = PreprocessForLogs(source);
            if (DebugDump) Dump(work);

            using var pix = BitmapToPix(work);
            using var page = _engine!.Process(pix, PageSegMode.SingleBlock);

            // Raw OCR text
            var text = page.GetText() ?? string.Empty;

            // Split to lines and normalize whitespace
            var lines = new List<string>();
            using (var sr = new StringReader(text))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (!string.IsNullOrWhiteSpace(line))
                        lines.Add(CleanupSpaces(line));
                }
            }

            // Merge wrapped lines: if a line doesn't start with "Day N," it's a continuation
            return MergeWrappedLines(lines);
        }

        // ------------------------------------------------------
        // Engine setup
        // ------------------------------------------------------
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
                    if (Directory.Exists(p)) { _tessdataPath = p; break; }
                }

                if (_tessdataPath == null)
                    throw new DirectoryNotFoundException(
                        $"tessdata folder not found. Checked:\n - {string.Join("\n - ", candidates)}");

                _engine = new TesseractEngine(_tessdataPath, "eng", EngineMode.LstmOnly);
                _engine.DefaultPageSegMode = PageSegMode.SingleBlock; // multi-line block

                // Bias Tesseract toward characters seen in tribe logs
                _engine.SetVariable("tessedit_char_whitelist",
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ,:+-()[]'!./");
                _engine.SetVariable("preserve_interword_spaces", "1");
            }
        }

        // ------------------------------------------------------
        // Preprocessing (HDR/SDR adaptive)
        // ------------------------------------------------------
        private static Bitmap PreprocessForLogs(Bitmap input)
        {
            // 1) Upscale a bit for OCR stability
            const double scale = 1.40;
            int w = Math.Max(1, (int)Math.Round(input.Width * scale));
            int h = Math.Max(1, (int)Math.Round(input.Height * scale));

            var up = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(up))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(input, new Rectangle(0, 0, w, h));
            }

            // 2) Adaptive tone-map (only meaningfully changes frames that are blown out)
            if (ToneMapEnabled) ToneMapAutoInPlace(up);

            // 3) Rec.709 luminance (preserves cyan/green/yellow text better than red-weighted)
            var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            ToLuma709Safe(up, gray);
            up.Dispose();

            // 4) Binarize (adaptive by default for mixed HDR regions; falls back to Otsu if disabled)
            var bin = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            if (AdaptiveEnabled)
            {
                // Window size ~31 gives good local contrast, c=7 biases slightly darker -> clearer strokes
                AdaptiveMeanBinarizeSafe(gray, bin, window: 31, c: 7);
            }
            else
            {
                OtsuBinarizeSafe(gray, bin);
            }
            gray.Dispose();

            // 5) Light morphology to close tiny gaps (dilate then erode with 3x3 cross)
            var closed = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            MorphClose3x3Safe(bin, closed);
            bin.Dispose();

            // 6) Mild sharpen to reinforce edges
            var sharp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            Convolve3x3Safe(closed, sharp, new float[,]
            {
                { 0, -1,  0 },
                { -1, 5, -1 },
                { 0, -1,  0 }
            });
            closed.Dispose();

            return sharp;
        }

        // ------------------------------------------------------
        // Helpers
        // ------------------------------------------------------
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

        private static string CleanupSpaces(string s)
        {
            // normalize weird interword gaps Tesseract sometimes emits
            return Regex.Replace(s, @"\s{2,}", " ");
        }

        private static List<string> MergeWrappedLines(List<string> lines)
        {
            var merged = new List<string>();
            var rxDay = new Regex(@"^\s*Day\s*\d+\s*,", RegexOptions.IgnoreCase);

            foreach (var line in lines)
            {
                if (rxDay.IsMatch(line) || merged.Count == 0)
                {
                    merged.Add(line);
                }
                else
                {
                    // continuation of previous line (wrap) -> append with space
                    merged[merged.Count - 1] = $"{merged[merged.Count - 1]} {line}";
                }
            }
            return merged;
        }

        // ---------- SAFE image ops (no 'unsafe' blocks) ----------

        /// <summary>
        /// Compress highlights if the 95th percentile is too bright, then apply a gentle gamma.
        /// On good SDR frames, scale≈1 and gamma≈1 so this becomes a no-op.
        /// </summary>
        private static void ToneMapAutoInPlace(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                int bytes = Math.Abs(stride) * h;
                var buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);

                var hist = new int[256];
                double sum = 0;

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
                        sum += (r + g + b) / 3.0;
                    }
                }

                int total = w * h;
                int target = (int)(total * 0.95);
                int acc = 0, p95 = 255;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) { p95 = i; break; } }

                double mean = sum / Math.Max(1, total);

                double scale = p95 > 235 ? 220.0 / p95 : 1.0; // pull down highlights
                double gamma = mean > 160 ? 1.35 : 1.0;       // darken slightly if overall bright

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

        /// <summary>RGB → Rec.709 luma grayscale (writes 24bppRgb with R=G=B=Y).</summary>
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

                        // Rec.709 coefficients
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
        /// Adaptive mean threshold using integral image. Good for mixed-brightness HDR UI.
        /// </summary>
        private static void AdaptiveMeanBinarizeSafe(Bitmap srcGray, Bitmap dst, int window, int c)
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

                // Build integral image over one channel (R == G == B)
                var integral = new int[(w + 1) * (h + 1)];
                for (int y = 1; y <= h; y++)
                {
                    int rowSum = 0;
                    int sRow = (y - 1) * ss;
                    for (int x = 1; x <= w; x++)
                    {
                        rowSum += sbuf[sRow + (x - 1) * 3];
                        int idx = y * (w + 1) + x;
                        integral[idx] = integral[idx - (w + 1)] + rowSum;
                    }
                }

                int r = Math.Max(1, window / 2);
                for (int y = 0; y < h; y++)
                {
                    int y0 = Math.Max(0, y - r);
                    int y1 = Math.Min(h - 1, y + r);
                    int dRow = y * ds;

                    for (int x = 0; x < w; x++)
                    {
                        int x0 = Math.Max(0, x - r);
                        int x1 = Math.Min(w - 1, x + r);

                        int A = integral[y0 * (w + 1) + x0];
                        int B = integral[y0 * (w + 1) + (x1 + 1)];
                        int C = integral[(y1 + 1) * (w + 1) + x0];
                        int D = integral[(y1 + 1) * (w + 1) + (x1 + 1)];

                        int area = (x1 - x0 + 1) * (y1 - y0 + 1);
                        int mean = (D - B - C + A) / Math.Max(1, area);

                        byte v = sbuf[y * ss + x * 3];
                        byte o = (byte)(v > mean - c ? 255 : 0);

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

        /// <summary>Global Otsu threshold (fallback mode).</summary>
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

        /// <summary>3x3 morphological close (dilate then erode) with a cross kernel.</summary>
        private static void MorphClose3x3Safe(Bitmap src, Bitmap dst)
        {
            int w = src.Width, h = src.Height;
            var rect = new Rectangle(0, 0, w, h);

            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var tmpData = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            var tData = tmpData.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int ss = sData.Stride, ts = tData.Stride, ds = dData.Stride;
                int bytesS = Math.Abs(ss) * h;
                int bytesT = Math.Abs(ts) * h;
                int bytesD = Math.Abs(ds) * h;

                var sbuf = new byte[bytesS];
                var tbuf = new byte[bytesT];
                var dbuf = new byte[bytesD];

                Marshal.Copy(sData.Scan0, sbuf, 0, bytesS);

                // DILATE
                for (int y = 0; y < h; y++)
                {
                    int tRow = y * ts;
                    for (int x = 0; x < w; x++)
                    {
                        byte max = 0;
                        for (int ky = -1; ky <= 1; ky++)
                        {
                            int yy = y + ky; if (yy < 0 || yy >= h) continue;
                            int sRow = yy * ss;
                            for (int kx = -1; kx <= 1; kx++)
                            {
                                if (Math.Abs(kx) + Math.Abs(ky) != 1 && !(kx == 0 && ky == 0)) continue; // cross
                                int xx = x + kx; if (xx < 0 || xx >= w) continue;
                                byte v = sbuf[sRow + xx * 3];
                                if (v > max) max = v;
                            }
                        }
                        int ti = tRow + x * 3;
                        tbuf[ti + 0] = max; tbuf[ti + 1] = max; tbuf[ti + 2] = max;
                    }
                }
                Marshal.Copy(tbuf, 0, tData.Scan0, bytesT);

                // ERODE
                for (int y = 0; y < h; y++)
                {
                    int dRow = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        byte min = 255;
                        for (int ky = -1; ky <= 1; ky++)
                        {
                            int yy = y + ky; if (yy < 0 || yy >= h) continue;
                            int tRow = yy * ts;
                            for (int kx = -1; kx <= 1; kx++)
                            {
                                if (Math.Abs(kx) + Math.Abs(ky) != 1 && !(kx == 0 && ky == 0)) continue; // cross
                                int xx = x + kx; if (xx < 0 || xx >= w) continue;
                                byte v = tbuf[tRow + xx * 3];
                                if (v < min) min = v;
                            }
                        }
                        int di = dRow + x * 3;
                        dbuf[di + 0] = min; dbuf[di + 1] = min; dbuf[di + 2] = min;
                    }
                }
                Marshal.Copy(dbuf, 0, dData.Scan0, bytesD);
            }
            finally
            {
                src.UnlockBits(sData);
                tmpData.UnlockBits(tData);
                dst.UnlockBits(dData);
                tmpData.Dispose();
            }
        }

        /// <summary>(Legacy) Red-weighted grayscale retained for reference (unused).</summary>
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
                        dbuf[di + 0] = v; dbuf[di + 1] = v; dbuf[di + 2] = v;
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
    }
}
