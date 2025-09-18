using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tesseract;
using SdImageFormat = System.Drawing.Imaging.ImageFormat;

namespace GravityCapture.Services
{
    /// <summary>
    /// HDR/SDR-tolerant OCR for ARK tribe logs.
    /// Pipeline (all safe): upscale → (optional) tone-map → HSV text mask →
    /// luma709 → mask → (adaptive|otsu) → open → majority → (optional) close → (optional) sharpen.
    /// Then Tesseract and robust splitting/normalization.
    /// </summary>
    public static class OcrService
    {
        // ---------- Engine ----------
        private static readonly object _lock = new();
        private static TesseractEngine? _engine;
        private static string? _tessdataPath;

        // ---------- Env toggles (defaults chosen to be helpful on HDR) ----------
        private static readonly bool DebugDump     = GetBool("GC_DEBUG_OCR", true);
        private static readonly bool ToneMap       = GetBool("GC_OCR_TONEMAP",  true);
        private static readonly bool Adaptive      = GetBool("GC_OCR_ADAPTIVE", true);
        private static readonly bool DoOpen        = GetBool("GC_OCR_OPEN",     true);   // new
        private static readonly bool DoMajority    = GetBool("GC_OCR_MAJORITY", true);   // new
        private static readonly bool DoClose       = GetBool("GC_OCR_CLOSE",    true);
        private static readonly bool DoSharpen     = GetBool("GC_OCR_SHARPEN",  false);

        // Tunables via env (string -> double/int)
        private static readonly double SatColor = GetDouble("GC_OCR_SAT_COLOR", 0.25);
        private static readonly double ValColor = GetDouble("GC_OCR_VAL_COLOR", 0.50);
        private static readonly double SatGray  = GetDouble("GC_OCR_SAT_GRAY",  0.12);
        private static readonly double ValGray  = GetDouble("GC_OCR_VAL_GRAY",  0.85);

        private static readonly int    AdaptWin  = ClampOdd(GetInt("GC_OCR_ADAPTIVE_WIN", 31), 3, 99);
        private static readonly int    AdaptC    = Math.Max(0, GetInt("GC_OCR_ADAPTIVE_C", 7));

        private static int _dumpIndex = 0;

        // ---------- Public ----------
        public static List<string> ReadLines(Bitmap source)
        {
            EnsureEngine();

            using var pre = Preprocess(source);
            if (DebugDump) Dump(pre, "final");

            using var pix  = BitmapToPix(pre);
            using var page = _engine!.Process(pix, PageSegMode.SingleBlock);
            var raw = page.GetText() ?? string.Empty;

            raw = raw.Replace("\r", "");
            raw = Regex.Replace(raw, @"[ \t]+", " ");

            var entries = SplitIntoEntries(raw);
            for (int i = 0; i < entries.Count; i++)
                entries[i] = NormalizeEntry(entries[i]);

            return entries;
        }

        public static Bitmap GetDebugPreview(Bitmap source) => Preprocess(source);

        // ---------- Engine ----------
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
                foreach (var p in candidates) if (Directory.Exists(p)) { _tessdataPath = p; break; }
                if (_tessdataPath == null)
                    throw new DirectoryNotFoundException("tessdata not found next to the app (tessdata/ or Assets/tessdata/).");

                _engine = new TesseractEngine(_tessdataPath, "eng", EngineMode.LstmOnly);
                _engine.DefaultPageSegMode = PageSegMode.SingleBlock;

                // Keep the alphabet tight to tribe logs
                _engine.SetVariable("tessedit_char_whitelist",
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ,:+-()[]'!./");
                _engine.SetVariable("preserve_interword_spaces", "1");
            }
        }

        // ---------- Preprocess ----------
        private static Bitmap Preprocess(Bitmap input)
        {
            // 1) upscale ~40% to help thin glyphs
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
            if (DebugDump) Dump(up, "a_up");

            // 2) tone-map for HDR glare
            if (ToneMap) { ToneMapAutoInPlace(up); if (DebugDump) Dump(up, "b_tonemap"); }

            // 3) HSV mask for colored text + bright grey timestamp
            var mask = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            BuildTextMaskHsv(up, mask, SatColor, ValColor, SatGray, ValGray);
            if (DebugDump) Dump(mask, "c_mask");

            // 4) grayscale (Rec.709)
            var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            ToLuma709Safe(up, gray);
            up.Dispose();
            if (DebugDump) Dump(gray, "d_gray");

            // 5) masked grayscale
            var masked = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            ApplyMaskSafe(gray, mask, masked);
            gray.Dispose();
            mask.Dispose();
            if (DebugDump) Dump(masked, "e_masked");

            // 6) binarize
            var mono = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            if (Adaptive) AdaptiveMeanBinarizeSafe(masked, mono, AdaptWin, AdaptC);
            else          OtsuBinarizeSafe(masked, mono);
            masked.Dispose();
            if (DebugDump) Dump(mono, "f_bin");

            // 7) open (erode→dilate) to kill salt/pepper commas
            if (DoOpen)
            {
                var opened = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                MorphOpen3x3Safe(mono, opened);
                mono.Dispose();
                mono = opened;
                if (DebugDump) Dump(mono, "g_open");
            }

            // 8) 3×3 majority filter (strong de-speckle that preserves strokes)
            if (DoMajority)
            {
                var maj = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                Majority3x3Safe(mono, maj);
                mono.Dispose();
                mono = maj;
                if (DebugDump) Dump(mono, "h_majority");
            }

            // 9) close (optional) to rejoin small gaps
            if (DoClose)
            {
                var closed = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                MorphClose3x3Safe(mono, closed);
                mono.Dispose();
                mono = closed;
                if (DebugDump) Dump(mono, "i_close");
            }

            // 10) (optional) mild sharpen
            if (DoSharpen)
            {
                var sharp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                Convolve3x3Safe(mono, sharp, new float[,] { { 0, -1, 0 }, { -1, 5, -1 }, { 0, -1, 0 } });
                mono.Dispose();
                mono = sharp;
                if (DebugDump) Dump(mono, "j_sharp");
            }

            return mono;
        }

        // ---------- Split + Normalize ----------
        private static List<string> SplitIntoEntries(string text)
        {
            var results = new List<string>();

            // Split at every Day XXXX, HH:MM:SS: marker – keep everything until the next marker.
            var rx = new Regex(@"(?<=^|\n)\s*Day\s*\d+\s*,\s*\d{1,2}:\d{2}:\d{2}\s*:", RegexOptions.IgnoreCase);
            var matches = rx.Matches(text);
            if (matches.Count == 0)
            {
                using var sr = new StringReader(text);
                for (string? line = sr.ReadLine(); line != null; line = sr.ReadLine())
                {
                    line = Regex.Replace(line, @"\s+", " ").Trim();
                    if (!string.IsNullOrWhiteSpace(line)) results.Add(line);
                }
                return results;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end   = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;
                string block = text.Substring(start, end - start);

                // collapse whitespace, but DO NOT chop at '!' (some lines end with '.')
                block = Regex.Replace(block, @"[ \t]+", " ").Trim();

                if (!string.IsNullOrWhiteSpace(block))
                    results.Add(block);
            }

            return results;
        }

        private static string NormalizeEntry(string s)
        {
            // Fix the common OCR artifacts we’ve seen in dumps
            s = s.Replace("Your,,", "Your ");
            s = s.Replace("Your, ", "Your ");
            s = s.Replace(" ,", " ");
            s = s.Replace("  ", " ");

            // “Jurret” -> “Turret”
            s = Regex.Replace(s, @"\bJurret\b", "Turret", RegexOptions.IgnoreCase);

            // Occasional bracket stuck at end: "... Turret']" -> "... Turret'"
            s = Regex.Replace(s, @"\]([!\.\)]|$)", "$1");

            // Merge "Item Cacheat" -> "Item Cache' at"
            s = Regex.Replace(s, @"Item Cache\s*at", "Item Cache' at", RegexOptions.IgnoreCase);

            // Normalize weird double colons that sometimes appear "Day 6031, 13:50:57:"
            s = Regex.Replace(s, @"(\d{1,2}:\d{2}:\d{2})\s*:", "$1:", RegexOptions.IgnoreCase);

            return s.Trim();
        }

        // ---------- Helpers ----------
        private static Pix BitmapToPix(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, SdImageFormat.Png);
            return Pix.LoadFromMemory(ms.ToArray());
        }

        private static void Dump(Bitmap bmp, string tag)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GravityCapture", "ocr-dump");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"frame-{_dumpIndex++:0000}-{tag}.png");
                bmp.Save(path, SdImageFormat.Png);
            }
            catch { /* ignore */ }
        }

        // ---------- Image ops (24bpp, safe) ----------
        private static void ToneMapAutoInPlace(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int w = bmp.Width, h = bmp.Height, s = data.Stride;
                var buf = new byte[s * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                // luminance histogram
                var hist = new int[256];
                for (int y = 0; y < h; y++)
                {
                    int r = y * s;
                    for (int x = 0; x < w; x++)
                    {
                        int i = r + x * 3;
                        byte B = buf[i + 0], G = buf[i + 1], R = buf[i + 2];
                        int Y = (int)(0.0722 * B + 0.7152 * G + 0.2126 * R + 0.5);
                        if (Y < 0) Y = 0; else if (Y > 255) Y = 255;
                        hist[Y]++;
                    }
                }

                // 98th percentile
                int total = w * h, goal = (int)(total * 0.98);
                int cum = 0, p98 = 255;
                for (int v = 0; v < 256; v++) { cum += hist[v]; if (cum >= goal) { p98 = v; break; } }
                if (p98 < 1) p98 = 1;

                double scale = 200.0 / p98;
                double gamma = 0.75;
                byte[] lut = new byte[256];
                for (int v = 0; v < 256; v++)
                {
                    double sNorm = v / 255.0;
                    double val = Math.Pow(Math.Min(1.0, sNorm * scale), gamma);
                    lut[v] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(val * 255.0)));
                }

                for (int y = 0; y < h; y++)
                {
                    int r = y * s;
                    for (int x = 0; x < w; x++)
                    {
                        int i = r + x * 3;
                        buf[i + 0] = lut[buf[i + 0]];
                        buf[i + 1] = lut[buf[i + 1]];
                        buf[i + 2] = lut[buf[i + 2]];
                    }
                }
                Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            }
            finally { bmp.UnlockBits(data); }
        }

        private static void BuildTextMaskHsv(Bitmap src, Bitmap dst, double satColor, double valColor, double satGray, double valGray)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = src.Width, h = src.Height;
                int ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        int si = sr + x * 3;
                        byte B = sb[si + 0], G = sb[si + 1], R = sb[si + 2];
                        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
                        double max = Math.Max(r, Math.Max(g, b));
                        double min = Math.Min(r, Math.Min(g, b));
                        double V = max;
                        double S = (max <= 0) ? 0 : (max - min) / max;

                        bool isColor      = (S >= satColor && V >= valColor);
                        bool isBrightGray = (S <= satGray  && V >= valGray);
                        byte v = (byte)((isColor || isBrightGray) ? 255 : 0);

                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = v;
                    }
                }

                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void ToLuma709Safe(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        int si = sr + x * 3;
                        byte B = sb[si + 0], G = sb[si + 1], R = sb[si + 2];
                        int Y = (int)(0.0722 * B + 0.7152 * G + 0.2126 * R + 0.5);
                        if (Y < 0) Y = 0; else if (Y > 255) Y = 255;
                        byte L = (byte)Y;

                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = L;
                    }
                }

                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void ApplyMaskSafe(Bitmap gray, Bitmap mask, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, gray.Width, gray.Height);
            var gData = gray.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var mData = mask.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect,  ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = gray.Width, h = gray.Height;
                int gs = gData.Stride, ms = mData.Stride, ds = dData.Stride;
                var gb = new byte[gs * h]; var mb = new byte[ms * h]; var db = new byte[ds * h];
                Marshal.Copy(gData.Scan0, gb, 0, gb.Length);
                Marshal.Copy(mData.Scan0, mb, 0, mb.Length);

                for (int y = 0; y < h; y++)
                {
                    int gr = y * gs, mr = y * ms, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        bool on = mb[mr + x * 3] > 127;
                        byte v = on ? gb[gr + x * 3] : (byte)0;
                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = v;
                    }
                }

                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { gray.UnlockBits(gData); mask.UnlockBits(mData); dst.UnlockBits(dData); }
        }

        private static void AdaptiveMeanBinarizeSafe(Bitmap srcGray, Bitmap dst, int window, int c)
        {
            window = ClampOdd(window, 3, 99);
            var rect = new Rectangle(0, 0, srcGray.Width, srcGray.Height);
            var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect,     ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = srcGray.Width, h = srcGray.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                long[,] integ = new long[h + 1, w + 1];
                for (int y = 1; y <= h; y++)
                {
                    long row = 0; int sr = (y - 1) * ss;
                    for (int x = 1; x <= w; x++)
                    {
                        byte g = sb[sr + (x - 1) * 3];
                        row += g; integ[y, x] = integ[y - 1, x] + row;
                    }
                }

                var db = new byte[ds * h];
                int r = window >> 1;

                for (int y = 0; y < h; y++)
                {
                    int dr = y * ds, sr = y * ss;
                    int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
                    int A = y0, B = y1 + 1;

                    for (int x = 0; x < w; x++)
                    {
                        int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                        int C = x0, D = x1 + 1;
                        long sum = integ[B, D] - integ[A, D] - integ[B, C] + integ[A, C];
                        int count = (x1 - x0 + 1) * (y1 - y0 + 1);
                        int mean  = (int)(sum / count);
                        byte pix  = sb[sr + x * 3];
                        byte bw   = (byte)((pix > (mean - c)) ? 255 : 0);

                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = bw;
                    }
                }

                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { srcGray.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void OtsuBinarizeSafe(Bitmap srcGray, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, srcGray.Width, srcGray.Height);
            var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect,     ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = srcGray.Width, h = srcGray.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                int[] hist = new int[256];
                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss;
                    for (int x = 0; x < w; x++) hist[sb[sr + x * 3]]++;
                }

                int total = w * h; double sum = 0;
                for (int t = 0; t < 256; t++) sum += t * hist[t];

                double sumB = 0; int wB = 0; double maxVar = -1; int thresh = 128;
                for (int t = 0; t < 256; t++)
                {
                    wB += hist[t]; if (wB == 0) continue;
                    int wF = total - wB; if (wF == 0) break;
                    sumB += t * hist[t];
                    double mB = sumB / wB, mF = (sum - sumB) / wF;
                    double var = wB * (double)wF * (mB - mF) * (mB - mF);
                    if (var > maxVar) { maxVar = var; thresh = t; }
                }

                var db = new byte[ds * h];
                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        byte bw = (byte)(sb[sr + x * 3] > thresh ? 255 : 0);
                        int di = dr + x * 3; db[di + 0] = db[di + 1] = db[di + 2] = bw;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { srcGray.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void MorphOpen3x3Safe(Bitmap src, Bitmap dst)
        {
            // erode then dilate
            var temp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            Erode3x3Safe(src, temp);
            Dilate3x3Safe(temp, dst);
            temp.Dispose();
        }

        private static void MorphClose3x3Safe(Bitmap src, Bitmap dst)
        {
            // dilate then erode
            var temp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            Dilate3x3Safe(src, temp);
            Erode3x3Safe(temp, dst);
            temp.Dispose();
        }

        private static void Erode3x3Safe(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                for (int y = 0; y < h; y++)
                {
                    int dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = 255;
                        for (int yy = -1; yy <= 1 && v == 255; yy++)
                        {
                            int ny = y + yy; if (ny < 0 || ny >= h) { v = 0; break; }
                            int nr = ny * ss;
                            for (int xx = -1; xx <= 1; xx++)
                            {
                                int nx = x + xx; if (nx < 0 || nx >= w) { v = 0; break; }
                                if (sb[nr + nx * 3] == 0) { v = 0; break; }
                            }
                        }
                        int di = dr + x * 3; db[di + 0] = db[di + 1] = db[di + 2] = v;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void Dilate3x3Safe(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                for (int y = 0; y < h; y++)
                {
                    int dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = 0;
                        for (int yy = -1; yy <= 1 && v == 0; yy++)
                        {
                            int ny = y + yy; if (ny < 0 || ny >= h) continue;
                            int nr = ny * ss;
                            for (int xx = -1; xx <= 1; xx++)
                            {
                                int nx = x + xx; if (nx < 0 || nx >= w) continue;
                                if (sb[nr + nx * 3] != 0) { v = 255; break; }
                            }
                        }
                        int di = dr + x * 3; db[di + 0] = db[di + 1] = db[di + 2] = v;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        // 3×3 majority: pixel becomes 255 if >=5 neighbors (incl self) are 255, else 0
        private static void Majority3x3Safe(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                for (int y = 0; y < h; y++)
                {
                    int dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        int count = 0;
                        for (int yy = -1; yy <= 1; yy++)
                        {
                            int ny = y + yy; if (ny < 0 || ny >= h) continue;
                            int nr = ny * ss;
                            for (int xx = -1; xx <= 1; xx++)
                            {
                                int nx = x + xx; if (nx < 0 || nx >= w) continue;
                                if (sb[nr + nx * 3] > 127) count++;
                            }
                        }
                        byte v = (byte)(count >= 5 ? 255 : 0);
                        int di = dr + x * 3; db[di + 0] = db[di + 1] = db[di + 2] = v;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void Convolve3x3Safe(Bitmap src, Bitmap dst, float[,] k)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly,  PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                for (int y = 0; y < h; y++)
                {
                    int dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        float acc = 0;
                        for (int yy = -1; yy <= 1; yy++)
                        {
                            int ny = y + yy; if (ny < 0 || ny >= h) continue;
                            int nr = ny * ss;
                            for (int xx = -1; xx <= 1; xx++)
                            {
                                int nx = x + xx; if (nx < 0 || nx >= w) continue;
                                acc += k[yy + 1, xx + 1] * sb[nr + nx * 3];
                            }
                        }
                        int v = (int)Math.Round(acc); v = Math.Max(0, Math.Min(255, v));
                        int di = dr + x * 3; db[di + 0] = db[di + 1] = db[di + 2] = (byte)v;
                    }
                }

                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        // ---------- Env helpers ----------
        private static bool   GetBool  (string key, bool def)   => !string.Equals(Environment.GetEnvironmentVariable(key), "0", StringComparison.OrdinalIgnoreCase) ? true : def == true && Environment.GetEnvironmentVariable(key) == null;
        private static int    GetInt   (string key, int def)    => int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : def;
        private static double GetDouble(string key, double def) => double.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : def;
        private static int    ClampOdd(int v, int min, int max)
        {
            v = Math.Max(min, Math.Min(max, v));
            if ((v & 1) == 0) v++;
            return v;
        }
    }
}
