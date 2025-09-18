using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tesseract;

// avoid clash with Tesseract.ImageFormat
using SdImageFormat = System.Drawing.Imaging.ImageFormat;

namespace GravityCapture.Services
{
    public static class OcrService
    {
        private static readonly object _lock = new();
        private static TesseractEngine? _engine;
        private static string? _tessdataPath;

        // ---- env toggles ----
        private static readonly bool DebugDump =
            string.Equals(Environment.GetEnvironmentVariable("GC_DEBUG_OCR"), "1", StringComparison.OrdinalIgnoreCase);

        private static readonly bool ToneMapEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_TONEMAP"), "0", StringComparison.OrdinalIgnoreCase);

        private static readonly bool AdaptiveEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_ADAPTIVE"), "0", StringComparison.OrdinalIgnoreCase);

        // NEW: allow disabling the HSV text mask entirely
        private static readonly bool MaskEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("GC_OCR_MASK"), "0", StringComparison.OrdinalIgnoreCase);

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

            var text = page.GetText() ?? string.Empty;
            text = Regex.Replace(text.Replace("\r", ""), @"[ \t]+", " ");

            return ExtractEntries(text);
        }

        // You can bind this to a “preview” UI if you want to show what OCR actually sees.
        public static Bitmap GetDebugPreview(Bitmap src) => PreprocessForLogs(src);

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
                    throw new DirectoryNotFoundException($"tessdata folder not found. Checked:\n - {string.Join("\n - ", candidates)}");

                _engine = new TesseractEngine(_tessdataPath, "eng", EngineMode.LstmOnly);
                _engine.DefaultPageSegMode = PageSegMode.SingleBlock;
                _engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ,:+-()[]'!./");
                _engine.SetVariable("preserve_interword_spaces", "1");
            }
        }

        // ------------------------------------------------------
        // Preprocess (dual-path with automatic selection)
        // ------------------------------------------------------
        private static Bitmap PreprocessForLogs(Bitmap input)
        {
            // 1) upscale for OCR stability
            const double scale = 1.40;
            int w = Math.Max(1, (int)Math.Round(input.Width * scale));
            int h = Math.Max(1, (int)Math.Round(input.Height * scale));

            var up = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(up))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode    = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(input, new Rectangle(0, 0, w, h));
            }

            // 2) gentle tone-map for blown highlights (no-op on SDR)
            if (ToneMapEnabled) ToneMapAutoInPlace(up);

            // 3) create TWO candidate pipelines and choose the better one
            Bitmap candidateMasked  = null!;
            Bitmap candidateNomask  = null!;
            double scoreMasked = -1, scoreNomask = -1;

            try
            {
                // ----- common gray -----
                var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                ToLuma709Safe(up, gray);

                // A) masked path (relaxed HSV + local-contrast rescue)
                if (MaskEnabled)
                {
                    using var mask = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    BuildTextMaskHybrid(up, gray, mask); // NOTE: uses both HSV and local contrast

                    using var masked = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    ApplyMaskSafe(gray, mask, masked);

                    candidateMasked = BinarizeMorphSharpen(masked);
                    scoreMasked = ScoreBinary(candidateMasked);
                }

                // B) no-mask path (pure adaptive threshold)
                candidateNomask = BinarizeMorphSharpen(gray);
                scoreNomask = ScoreBinary(candidateNomask);

                // choose best
                Bitmap chosen;
                double chosenScore;
                if (scoreMasked > scoreNomask * 1.05) // a little bias toward masked when clearly better
                {
                    chosen = candidateMasked;
                    candidateNomask.Dispose();
                    chosenScore = scoreMasked;
                }
                else
                {
                    chosen = candidateNomask;
                    if (candidateMasked != null) candidateMasked.Dispose();
                    chosenScore = scoreNomask;
                }

                // safety: if score looks anemic (e.g., only timestamps survived), OR the white ratio too low,
                // try OR-combining both (helps in borderline HDR color text)
                if (scoreMasked >= 0 && scoreNomask >= 0)
                {
                    var ratio = WhiteRatio(chosen);
                    if (ratio < 0.002 || chosenScore < 150) // very little signal
                    {
                        using var fused = OrBinary(candidateMasked ?? chosen, candidateNomask ?? chosen);
                        var fusedScore = ScoreBinary(fused);
                        if (fusedScore > chosenScore)
                        {
                            chosen.Dispose();
                            return CloneBitmap(fused);
                        }
                    }
                }

                return chosen;
            }
            finally
            {
                up.Dispose();
                // dispose gray is handled in the paths
            }
        }

        // Build text mask using a **relaxed HSV** gate + **local-contrast** rescue.
        // This fixes the issue where colored UI text was dropped by a strict saturation/value mask.
        private static void BuildTextMaskHybrid(Bitmap srcColor, Bitmap srcGray, Bitmap dstMask)
        {
            int w = srcColor.Width, h = srcColor.Height;
            var rect = new Rectangle(0, 0, w, h);

            var cData = srcColor.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var gData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dstMask.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int cs = cData.Stride, gs = gData.Stride, ds = dData.Stride;
                int cb = Math.Abs(cs) * h, gb = Math.Abs(gs) * h, db = Math.Abs(ds) * h;
                var cbuf = new byte[cb]; Marshal.Copy(cData.Scan0, cbuf, 0, cb);
                var gbuf = new byte[gb]; Marshal.Copy(gData.Scan0, gbuf, 0, gb);
                var dbuf = new byte[db];

                // Build an integral image for local mean (one channel is enough)
                var integral = new int[(w + 1) * (h + 1)];
                for (int y = 1; y <= h; y++)
                {
                    int sumRow = 0;
                    int sRow = (y - 1) * gs;
                    for (int x = 1; x <= w; x++)
                    {
                        sumRow += gbuf[sRow + (x - 1) * 3];
                        integral[y * (w + 1) + x] = integral[(y - 1) * (w + 1) + x] + sumRow;
                    }
                }

                int r = 7;          // ~15x15 local window
                int cBoost = 10;    // required contrast over local mean

                for (int y = 0; y < h; y++)
                {
                    int crow = y * cs;
                    int grow = y * gs;
                    int drow = y * ds;

                    int y0 = Math.Max(0, y - r);
                    int y1 = Math.Min(h - 1, y + r);

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

                        byte b = cbuf[crow + x * 3 + 0];
                        byte g = cbuf[crow + x * 3 + 1];
                        byte rch = cbuf[crow + x * 3 + 2];

                        int max = Math.Max(rch, Math.Max(g, b));
                        int min = Math.Min(rch, Math.Min(g, b));
                        double V = max / 255.0;
                        double S = max == 0 ? 0.0 : (max - min) / (double)max;

                        byte yy = gbuf[grow + x * 3];

                        // RELAXED gates:
                        //  - colored text:  S >= 0.08 and V >= 0.28 (much looser)
                        //  - gray timestamp: S <= 0.28 and V >= 0.70 (looser too)
                        //  - local-contrast rescue: gray - localMean >= 10 (captures faint colored strokes)
                        bool colored = (S >= 0.08 && V >= 0.28);
                        bool gray    = (S <= 0.28 && V >= 0.70);
                        bool contrast= (yy - mean) >= cBoost;

                        byte m = (byte)((colored || gray || contrast) ? 255 : 0);

                        dbuf[drow + x * 3 + 0] = m;
                        dbuf[drow + x * 3 + 1] = m;
                        dbuf[drow + x * 3 + 2] = m;
                    }
                }

                Marshal.Copy(dbuf, 0, dData.Scan0, db);
            }
            finally
            {
                srcColor.UnlockBits(cData);
                srcGray.UnlockBits(gData);
                dstMask.UnlockBits(dData);
            }
        }

        private static Bitmap BinarizeMorphSharpen(Bitmap grayOrMasked)
        {
            int w = grayOrMasked.Width, h = grayOrMasked.Height;

            var bin = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            if (AdaptiveEnabled)
                AdaptiveMeanBinarizeSafe(grayOrMasked, bin, window: 31, c: 7);
            else
                OtsuBinarizeSafe(grayOrMasked, bin);

            var closed = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            MorphClose3x3Safe(bin, closed);
            bin.Dispose();

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
        // Text scoring & utilities
        // ------------------------------------------------------
        private static double ScoreBinary(Bitmap bin)
        {
            // score by horizontal+vertical black/white transitions + reasonable white coverage
            int w = bin.Width, h = bin.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bin.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int s = data.Stride;
                int bytes = Math.Abs(s) * h;
                var buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);

                int samplesY = Math.Max(8, h / 60);
                int samplesX = Math.Max(8, w / 60);

                long transitions = 0;
                long white = 0;

                // horizontal
                for (int sy = 1; sy < samplesY; sy++)
                {
                    int y = sy * h / samplesY;
                    int row = y * s;
                    bool prev = buf[row] > 127;
                    for (int x = 1; x < w; x++)
                    {
                        bool cur = buf[row + x * 3] > 127;
                        if (cur != prev) transitions++;
                        prev = cur;
                    }
                }
                // vertical
                for (int sx = 1; sx < samplesX; sx++)
                {
                    int x = sx * w / samplesX;
                    bool prev = buf[x * 3] > 127;
                    for (int y = 1; y < h; y++)
                    {
                        bool cur = buf[y * s + x * 3] > 127;
                        if (cur != prev) transitions++;
                        prev = cur;
                    }
                }

                for (int y = 0; y < h; y++)
                {
                    int row = y * s;
                    for (int x = 0; x < w; x++)
                        if (buf[row + x * 3] > 127) white++;
                }

                double whiteRatio = (double)white / (w * (double)h);

                // reward text-like density (not too sparse, not too dense)
                double ratioBoost =
                    whiteRatio < 0.0008 ? 0.4 :
                    whiteRatio > 0.35   ? 0.6 :
                    1.0 + Math.Min(whiteRatio, 0.18) * 2.5;

                return transitions * ratioBoost;
            }
            finally { bin.UnlockBits(data); }
        }

        private static double WhiteRatio(Bitmap bin)
        {
            int w = bin.Width, h = bin.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bin.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int s = data.Stride;
                long white = 0;
                for (int y = 0; y < h; y++)
                {
                    int row = y * s;
                    for (int x = 0; x < w; x++)
                        if (((int)Marshal.ReadByte(data.Scan0, row + x * 3)) > 127) white++;
                }
                return white / (double)(w * h);
            }
            finally { bin.UnlockBits(data); }
        }

        private static Bitmap OrBinary(Bitmap a, Bitmap b)
        {
            int w = Math.Min(a.Width, b.Width);
            int h = Math.Min(a.Height, b.Height);
            var outBmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);

            var rect = new Rectangle(0, 0, w, h);
            var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var doo = outBmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int sa = da.Stride, sb = db.Stride, so = doo.Stride;
                for (int y = 0; y < h; y++)
                {
                    int ra = y * sa, rb = y * sb, ro = y * so;
                    for (int x = 0; x < w; x++)
                    {
                        byte va = Marshal.ReadByte(da.Scan0, ra + x * 3);
                        byte vb = Marshal.ReadByte(db.Scan0, rb + x * 3);
                        byte v = (byte)((va > 127 || vb > 127) ? 255 : 0);
                        Marshal.WriteByte(doo.Scan0, ro + x * 3 + 0, v);
                        Marshal.WriteByte(doo.Scan0, ro + x * 3 + 1, v);
                        Marshal.WriteByte(doo.Scan0, ro + x * 3 + 2, v);
                    }
                }
            }
            finally
            {
                a.UnlockBits(da);
                b.UnlockBits(db);
                outBmp.UnlockBits(doo);
            }
            return outBmp;
        }

        private static Bitmap CloneBitmap(Bitmap src)
        {
            var b = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(b);
            g.DrawImage(src, 0, 0);
            return b;
        }

        // ------------------------------------------------------
        // Entry extraction & utils
        // ------------------------------------------------------
        private static List<string> ExtractEntries(string text)
        {
            var rx = new Regex(@"(?<=^|\n)\s*Day\s*\d+\s*,\s*\d{1,2}:\d{2}:\d{2}\s*:", RegexOptions.IgnoreCase);
            var matches = rx.Matches(text);

            var results = new List<string>();
            if (matches.Count == 0)
            {
                using var sr = new StringReader(text);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
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
                int bang = block.IndexOf('!');
                if (bang >= 0) block = block.Substring(0, bang + 1);

                if (!string.IsNullOrWhiteSpace(block))
                    results.Add(block);
            }

            return results;
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
            catch { }
        }

        // ------------------------------------------------------
        // Image ops (safe)
        // ------------------------------------------------------
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
                        byte b = buf[i + 0], g = buf[i + 1], r = buf[i + 2];
                        byte m = (byte)Math.Max(r, Math.Max(g, b));
                        hist[m]++; sum += (r + g + b) / 3.0;
                    }
                }

                int total = w * h, target = (int)(total * 0.95);
                int acc = 0, p95 = 255;
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) { p95 = i; break; } }

                double mean = sum / Math.Max(1, total);
                double scale = p95 > 235 ? 220.0 / p95 : 1.0;
                double gamma = mean > 160 ? 1.25 : 1.0;

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
            finally { bmp.UnlockBits(data); }
        }

        private static void ToLuma709Safe(Bitmap src, Bitmap dst)
        {
            int w = src.Width, h = src.Height;
            var rect = new Rectangle(0, 0, w, h);

            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int ss = sData.Stride, ds = dData.Stride;
                int bytesS = Math.Abs(ss) * h, bytesD = Math.Abs(ds) * h;
                var sbuf = new byte[bytesS]; Marshal.Copy(sData.Scan0, sbuf, 0, bytesS);
                var dbuf = new byte[bytesD];

                for (int y = 0; y < h; y++)
                {
                    int sRow = y * ss, dRow = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        int si = sRow + x * 3;
                        byte b = sbuf[si + 0], g = sbuf[si + 1], r = sbuf[si + 2];
                        int yv = (int)Math.Round(0.0722 * b + 0.7152 * g + 0.2126 * r);
                        if (yv < 0) yv = 0; if (yv > 255) yv = 255;
                        byte yy = (byte)yv;

                        int di = dRow + x * 3;
                        dbuf[di + 0] = yy; dbuf[di + 1] = yy; dbuf[di + 2] = yy;
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

        private static void ApplyMaskSafe(Bitmap gray, Bitmap mask, Bitmap dst)
        {
            int w = gray.Width, h = gray.Height;
            var rect = new Rectangle(0, 0, w, h);

            var gData = gray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var mData = mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int gs = gData.Stride, ms = mData.Stride, ds = dData.Stride;
                int bytesG = Math.Abs(gs) * h, bytesM = Math.Abs(ms) * h, bytesD = Math.Abs(ds) * h;
                var gbuf = new byte[bytesG]; Marshal.Copy(gData.Scan0, gbuf, 0, bytesG);
                var mbuf = new byte[bytesM]; Marshal.Copy(mData.Scan0, mbuf, 0, bytesM);
                var dbuf = new byte[bytesD];

                for (int y = 0; y < h; y++)
                {
                    int gr = y * gs, mr = y * ms, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        byte m = mbuf[mr + x * 3];
                        byte v = (byte)(m == 0 ? 0 : gbuf[gr + x * 3]);

                        dbuf[dr + x * 3 + 0] = v;
                        dbuf[dr + x * 3 + 1] = v;
                        dbuf[dr + x * 3 + 2] = v;
                    }
                }
                Marshal.Copy(dbuf, 0, dData.Scan0, bytesD);
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
            int w = srcGray.Width, h = srcGray.Height;
            var rect = new Rectangle(0, 0, w, h);

            var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int ss = sData.Stride, ds = dData.Stride;
                int bytesS = Math.Abs(ss) * h, bytesD = Math.Abs(ds) * h;
                var sbuf = new byte[bytesS]; Marshal.Copy(sData.Scan0, sbuf, 0, bytesS);
                var dbuf = new byte[bytesD];

                var integral = new int[(w + 1) * (h + 1)];
                for (int y = 1; y <= h; y++)
                {
                    int rowSum = 0;
                    int sRow = (y - 1) * ss;
                    for (int x = 1; x <= w; x++)
                    {
                        rowSum += sbuf[sRow + (x - 1) * 3];
                        integral[y * (w + 1) + x] = integral[(y - 1) * (w + 1) + x] + rowSum;
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

        private static void Ots
