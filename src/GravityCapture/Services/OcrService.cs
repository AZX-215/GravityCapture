using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tesseract;

// avoid conflict with Tesseract.ImageFormat
using SdImageFormat = System.Drawing.Imaging.ImageFormat;

namespace GravityCapture.Services
{
    /// <summary>
    /// OCR pipeline tuned for ARK tribe logs in SDR and HDR.
    /// Fully safe (no 'unsafe' blocks). Uses adaptive tone-mapping,
    /// HSV text masking, grayscale, binarization, and small morphology.
    /// </summary>
    public static class OcrService
    {
        private static readonly object _lock = new();
        private static TesseractEngine? _engine;
        private static string? _tessdataPath;

        // --- Environment toggles ---
        private static readonly bool DebugDump =
            string.Equals(Environment.GetEnvironmentVariable("GC_DEBUG_OCR"), "1", StringComparison.OrdinalIgnoreCase);

        private static readonly bool ToneMapEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_TONEMAP"), "0", StringComparison.OrdinalIgnoreCase);

        private static readonly bool AdaptiveEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_ADAPTIVE"), "0", StringComparison.OrdinalIgnoreCase);

        private static readonly bool CloseEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_CLOSE"), "0", StringComparison.OrdinalIgnoreCase);

        private static readonly bool SharpenEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_SHARPEN"), "0", StringComparison.OrdinalIgnoreCase);

        private static int _dumpIndex = 0;

        // ------------------------------------------------------
        // Public API
        // ------------------------------------------------------
        public static List<string> ReadLines(Bitmap source)
        {
            EnsureEngine();

            using var pre = PreprocessForLogs(source);
            if (DebugDump) Dump(pre);

            using var pix = BitmapToPix(pre);
            using var page = _engine!.Process(pix, PageSegMode.SingleBlock);
            var text = page.GetText() ?? string.Empty;

            // normalize whitespace for stable splitting
            text = text.Replace("\r", "");
            text = Regex.Replace(text, @"[ \t]+", " ");

            return SplitIntoEntries(text);
        }

        public static Bitmap GetDebugPreview(Bitmap source) => PreprocessForLogs(source);

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
                    Path.Combine(baseDir, "Assets", "tessdata")
                };

                foreach (var p in candidates)
                {
                    if (Directory.Exists(p)) { _tessdataPath = p; break; }
                }

                if (_tessdataPath == null)
                    throw new DirectoryNotFoundException(
                        "tessdata not found. Looked in:\n - " + string.Join("\n - ", candidates));

                _engine = new TesseractEngine(_tessdataPath, "eng", EngineMode.LstmOnly);
                _engine.DefaultPageSegMode = PageSegMode.SingleBlock;

                // whitelist common tribe-log chars
                _engine.SetVariable("tessedit_char_whitelist",
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ,:+-()[]'!./");
                _engine.SetVariable("preserve_interword_spaces", "1");
            }
        }

        // ------------------------------------------------------
        // Preprocess pipeline
        // ------------------------------------------------------
        private static Bitmap PreprocessForLogs(Bitmap input)
        {
            // 1) upscale a bit
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

            // 2) HDR tone map (no-op for SDR-like input)
            if (ToneMapEnabled) ToneMapAutoInPlace(up);

            // 3) HSV mask to isolate text (colored + bright gray timestamps)
            var mask = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            BuildTextMaskHsv(up, mask, satColor: 0.25, valColor: 0.50, satGray: 0.12, valGray: 0.85);

            // 4) Convert to Rec.709 luma grayscale
            var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            ToLuma709Safe(up, gray);
            up.Dispose();

            // 5) Apply mask
            var masked = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            ApplyMaskSafe(gray, mask, masked);
            gray.Dispose();
            mask.Dispose();

            // 6) Binarize
            var mono = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            if (AdaptiveEnabled)
                AdaptiveMeanBinarizeSafe(masked, mono, window: 31, c: 7);
            else
                OtsuBinarizeSafe(masked, mono);
            masked.Dispose();

            // 7) Morph close (optional)
            if (CloseEnabled)
            {
                var closed = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                MorphClose3x3Safe(mono, closed);
                mono.Dispose();
                mono = closed;
            }

            // 8) Mild sharpen (optional)
            if (SharpenEnabled)
            {
                var sharp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                Convolve3x3Safe(mono, sharp, new float[,]
                {
                    { 0, -1,  0 },
                    { -1,  5, -1 },
                    { 0, -1,  0 }
                });
                mono.Dispose();
                mono = sharp;
            }

            return mono;
        }

        // ------------------------------------------------------
        // Split OCR block into tribe-log entries
        // ------------------------------------------------------
        private static List<string> SplitIntoEntries(string text)
        {
            var results = new List<string>();

            // split on "Day N, HH:MM:SS:"
            var rx = new Regex(@"(?<=^|\n)\s*Day\s*\d+\s*,\s*\d{1,2}:\d{2}:\d{2}\s*:", RegexOptions.IgnoreCase);
            var matches = rx.Matches(text);

            if (matches.Count == 0)
            {
                using var sr = new StringReader(text);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = Regex.Replace(line, @"\s+", " ").Trim();
                    if (!string.IsNullOrWhiteSpace(line)) results.Add(line);
                }
                return results;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;
                string block = text.Substring(start, end - start);

                block = Regex.Replace(block, @"\s+", " ").Trim();

                // Heuristic: keep only up to the first '!' if multiple present
                int bang = block.IndexOf('!');
                if (bang >= 0) block = block.Substring(0, bang + 1);

                if (!string.IsNullOrWhiteSpace(block)) results.Add(block);
            }

            return results;
        }

        // ------------------------------------------------------
        // Utilities
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
                var path = Path.Combine(dir, $"frame-{_dumpIndex++:0000}.png");
                bmp.Save(path, SdImageFormat.Png);
            }
            catch { /* ignore */ }
        }

        // ------------------------------------------------------
        // Image processing helpers (24bpp BGR)
        // ------------------------------------------------------

        /// <summary>
        /// Simple auto tone-map: compress highlights using a gamma curve scaled
        /// by the 98th-percentile luminance so HDR bloom doesn't wash text out.
        /// </summary>
        private static void ToneMapAutoInPlace(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);

            try
            {
                int stride = data.Stride;
                int w = bmp.Width;
                int h = bmp.Height;
                int bytes = stride * h;

                var buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);

                // Build luminance histogram
                var hist = new int[256];
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 3;
                        byte b = buf[i + 0];
                        byte g = buf[i + 1];
                        byte r = buf[i + 2];
                        int y709 = (int)(0.0722 * b + 0.7152 * g + 0.2126 * r + 0.5);
                        if (y709 < 0) y709 = 0; else if (y709 > 255) y709 = 255;
                        hist[y709]++;
                    }
                }

                // 98th percentile
                int total = w * h;
                int target = (int)(total * 0.98);
                int cum = 0;
                int p98 = 255;
                for (int v = 0; v < 256; v++)
                {
                    cum += hist[v];
                    if (cum >= target) { p98 = v; break; }
                }
                if (p98 < 1) p98 = 1;

                double scale = 200.0 / p98; // bring 98th pctl near 200
                double gamma = 0.75;        // compress highlights

                // build LUT
                var lut = new byte[256];
                for (int v = 0; v < 256; v++)
                {
                    double s = v / 255.0;
                    double val = Math.Pow(Math.Min(1.0, s * scale), gamma);
                    int outv = (int)Math.Round(val * 255.0);
                    if (outv < 0) outv = 0; else if (outv > 255) outv = 255;
                    lut[v] = (byte)outv;
                }

                // apply per channel
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
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>
        /// Create a binary mask selecting colored UI text (S>=satColor & V>=valColor)
        /// and the bright gray timestamps (S<=satGray & V>=valGray)
        /// </summary>
        private static void BuildTextMaskHsv(Bitmap src, Bitmap dst, double satColor, double valColor, double satGray, double valGray)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = src.Width;
                int h = src.Height;
                int sStride = sData.Stride;
                int dStride = dData.Stride;

                var sbuf = new byte[sStride * h];
                var dbuf = new byte[dStride * h];

                Marshal.Copy(sData.Scan0, sbuf, 0, sbuf.Length);

                for (int y = 0; y < h; y++)
                {
                    int sRow = y * sStride;
                    int dRow = y * dStride;

                    for (int x = 0; x < w; x++)
                    {
                        int si = sRow + x * 3;
                        byte b = sbuf[si + 0];
                        byte g = sbuf[si + 1];
                        byte r = sbuf[si + 2];

                        // HSV
                        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
                        double max = Math.Max(rd, Math.Max(gd, bd));
                        double min = Math.Min(rd, Math.Min(gd, bd));
                        double v = max;
                        double s = (max <= 0) ? 0 : (max - min) / max;

                        bool isColor = (s >= satColor && v >= valColor);
                        bool isBrightGray = (s <= satGray && v >= valGray);

                        bool on = isColor || isBrightGray;

                        int di = dRow + x * 3;
                        byte val = on ? (byte)255 : (byte)0;
                        dbuf[di + 0] = val;
                        dbuf[di + 1] = val;
                        dbuf[di + 2] = val;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, dbuf.Length);
            }
            finally
            {
                src.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }

        private static void ToLuma709Safe(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = src.Width;
                int h = src.Height;
                int sStride = sData.Stride;
                int dStride = dData.Stride;

                var sbuf = new byte[sStride * h];
                var dbuf = new byte[dStride * h];

                Marshal.Copy(sData.Scan0, sbuf, 0, sbuf.Length);

                for (int y = 0; y < h; y++)
                {
                    int sRow = y * sStride;
                    int dRow = y * dStride;

                    for (int x = 0; x < w; x++)
                    {
                        int si = sRow + x * 3;
                        byte b = sbuf[si + 0];
                        byte g = sbuf[si + 1];
                        byte r = sbuf[si + 2];

                        int lum = (int)(0.0722 * b + 0.7152 * g + 0.2126 * r + 0.5);
                        if (lum < 0) lum = 0; else if (lum > 255) lum = 255;
                        byte L = (byte)lum;

                        int di = dRow + x * 3;
                        dbuf[di + 0] = L;
                        dbuf[di + 1] = L;
                        dbuf[di + 2] = L;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, dbuf.Length);
            }
            finally
            {
                src.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }

        private static void ApplyMaskSafe(Bitmap gray, Bitmap mask, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, gray.Width, gray.Height);
            var gData = gray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var mData = mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = gray.Width;
                int h = gray.Height;
                int gs = gData.Stride;
                int ms = mData.Stride;
                int ds = dData.Stride;

                var gbuf = new byte[gs * h];
                var mbuf = new byte[ms * h];
                var dbuf = new byte[ds * h];

                Marshal.Copy(gData.Scan0, gbuf, 0, gbuf.Length);
                Marshal.Copy(mData.Scan0, mbuf, 0, mbuf.Length);

                for (int y = 0; y < h; y++)
                {
                    int gr = y * gs;
                    int mr = y * ms;
                    int dr = y * ds;

                    for (int x = 0; x < w; x++)
                    {
                        int gi = gr + x * 3;
                        int mi = mr + x * 3;
                        int di = dr + x * 3;

                        bool on = mbuf[mi] > 127;
                        byte v = on ? gbuf[gi] : (byte)0;

                        dbuf[di + 0] = v;
                        dbuf[di + 1] = v;
                        dbuf[di + 2] = v;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, dbuf.Length);
            }
            finally
            {
                gray.UnlockBits(gData);
                mask.UnlockBits(mData);
                dst.UnlockBits(dData);
            }
        }

        private static void AdaptiveMeanBinarizeSafe(Bitmap srcGray, Bitmap dst, int window, int c)
        {
            if (window < 3) window = 3;
            if ((window & 1) == 0) window++; // odd

            var rect = new Rectangle(0, 0, srcGray.Width, srcGray.Height);
            var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = srcGray.Width;
                int h = srcGray.Height;
                int ss = sData.Stride;
                int ds = dData.Stride;

                var sbuf = new byte[ss * h];
                Marshal.Copy(sData.Scan0, sbuf, 0, sbuf.Length);

                // integral image of grayscale (use blue channel as they are equal)
                long[,] integ = new long[h + 1, w + 1];

                for (int y = 1; y <= h; y++)
                {
                    long rowsum = 0;
                    int row = (y - 1) * ss;
                    for (int x = 1; x <= w; x++)
                    {
                        byte val = sbuf[row + (x - 1) * 3];
                        rowsum += val;
                        integ[y, x] = integ[y - 1, x] + rowsum;
                    }
                }

                var dbuf = new byte[ds * h];
                int r = window >> 1;

                for (int y = 0; y < h; y++)
                {
                    int dr = y * ds;
                    int sy = y * ss;

                    int y0 = Math.Max(0, y - r);
                    int y1 = Math.Min(h - 1, y + r);
                    int A = y0;
                    int B = y1 + 1;

                    for (int x = 0; x < w; x++)
                    {
                        int x0 = Math.Max(0, x - r);
                        int x1 = Math.Min(w - 1, x + r);

                        int C = x0;
                        int D = x1 + 1;

                        long sum = integ[B, D] - integ[A, D] - integ[B, C] + integ[A, C];
                        int count = (x1 - x0 + 1) * (y1 - y0 + 1);
                        int mean = (int)(sum / count);

                        int gi = sy + x * 3;
                        byte pix = sbuf[gi];

                        byte bw = (byte)((pix > (mean - c)) ? 255 : 0);

                        int di = dr + x * 3;
                        dbuf[di + 0] = bw;
                        dbuf[di + 1] = bw;
                        dbuf[di + 2] = bw;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, dbuf.Length);
            }
            finally
            {
                srcGray.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }

        private static void OtsuBinarizeSafe(Bitmap srcGray, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, srcGray.Width, srcGray.Height);
            var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = srcGray.Width;
                int h = srcGray.Height;
                int ss = sData.Stride;
                int ds = dData.Stride;

                var sbuf = new byte[ss * h];
                Marshal.Copy(sData.Scan0, sbuf, 0, sbuf.Length);

                int[] hist = new int[256];
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

                double sumB = 0;
                int wB = 0;
                int wF = 0;
                double maxVar = -1;
                int thresh = 128;

                for (int t = 0; t < 256; t++)
                {
                    wB += hist[t];
                    if (wB == 0) continue;
                    wF = total - wB;
                    if (wF == 0) break;

                    sumB += t * hist[t];
                    double mB = sumB / wB;
                    double mF = (sum - sumB) / wF;
                    double varBetween = wB * wF * (mB - mF) * (mB - mF);

                    if (varBetween > maxVar)
                    {
                        maxVar = varBetween;
                        thresh = t;
                    }
                }

                var dbuf = new byte[ds * h];
                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss;
                    int dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = sbuf[sr + x * 3];
                        byte bw = (v > thresh) ? (byte)255 : (byte)0;

                        int di = dr + x * 3;
                        dbuf[di + 0] = bw;
                        dbuf[di + 1] = bw;
                        dbuf[di + 2] = bw;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, dbuf.Length);
            }
            finally
            {
                srcGray.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }

        /// <summary>
        /// Morphological close with 3x3 square (dilate then erode).
        /// Operates on binary images (0/255).
        /// </summary>
        private static void MorphClose3x3Safe(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = src.Width;
                int h = src.Height;
                int ss = sData.Stride;
                int ds = dData.Stride;

                var sbuf = new byte[ss * h];
                Marshal.Copy(sData.Scan0, sbuf, 0, sbuf.Length);

                // temp for dilation
                var dil = new byte[ss * h];

                // DILATE
                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss;
                    int dr = y * ss;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = 0;
                        for (int yy = -1; yy <= 1; yy++)
                        {
                            int ny = y + yy;
                            if (ny < 0 || ny >= h) continue;
                            int nr = ny * ss;
                            for (int xx = -1; xx <= 1; xx++)
                            {
                                int nx = x + xx;
                                if (nx < 0 || nx >= w) continue;
                                if (sbuf[nr + nx * 3] > 0) { v = 255; goto doneDil; }
                            }
                        }
                    doneDil:
                        int di = dr + x * 3;
                        dil[di + 0] = v;
                        dil[di + 1] = v;
                        dil[di + 2] = v;
                    }
                }

                // ERODE on dilated
                var dbuf = new byte[ds * h];
                for (int y = 0; y < h; y++)
                {
                    int dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = 255;
                        for (int yy = -1; yy <= 1; yy++)
                        {
                            int ny = y + yy;
                            if (ny < 0 || ny >= h) { v = 0; break; }
                            int nr = ny * ss;
                            for (int xx = -1; xx <= 1; xx++)
                            {
                                int nx = x + xx;
                                if (nx < 0 || nx >= w) { v = 0; break; }
                                if (dil[nr + nx * 3] == 0) { v = 0; break; }
                            }
                            if (v == 0) break;
                        }

                        int di = dr + x * 3;
                        dbuf[di + 0] = v;
                        dbuf[di + 1] = v;
                        dbuf[di + 2] = v;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, dbuf.Length);
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
                int w = src.Width;
                int h = src.Height;
                int ss = sData.Stride;
                int ds = dData.Stride;

                var sbuf = new byte[ss * h];
                var dbuf = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sbuf, 0, sbuf.Length);

                for (int y = 0; y < h; y++)
                {
                    int dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int yy = -1; yy <= 1; yy++)
                        {
                            int ny = y + yy;
                            if (ny < 0 || ny >= h) continue;
                            int nr = ny * ss;
                            for (int xx = -1; xx <= 1; xx++)
                            {
                                int nx = x + xx;
                                if (nx < 0 || nx >= w) continue;
                                float kv = k[yy + 1, xx + 1];
                                acc += kv * sbuf[nr + nx * 3];
                            }
                        }
                        int v = (int)Math.Round(acc);
                        if (v < 0) v = 0; else if (v > 255) v = 255;
                        byte vb = (byte)v;

                        int di = dr + x * 3;
                        dbuf[di + 0] = vb;
                        dbuf[di + 1] = vb;
                        dbuf[di + 2] = vb;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, dbuf.Length);
            }
            finally
            {
                src.UnlockBits(sData);
                dst.UnlockBits(dData);
            }
        }
    }
}
