using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tesseract;
using SdImageFormat = System.Drawing.Imaging.ImageFormat;

namespace GravityCapture.Services
{
    /// <summary>
    /// OCR pipeline: upscale → tonemap → mask → grayscale → (CLAHE/gamma/preblur/contrast)
    /// → binarize (sauvola/wolf/adaptive/OTSU/dual-threshold) → invert → open/majority/close
    /// → distance-thicken → fill-holes → remove-dots → sharpen → Tesseract.
    /// All added stages are opt-in via env/profile flags with safe defaults.
    /// </summary>
    public static class OcrService
    {
        // ---------- Engine ----------
        private static readonly object _lock = new();
        private static TesseractEngine? _engine;
        private static string? _tessdataPath;

        // ---------- Core toggles ----------
        private static readonly bool DebugDump  = GetBool("GC_DEBUG_OCR", true);
        private static readonly bool ToneMap    = GetBool("GC_OCR_TONEMAP", true);
        private static readonly bool Adaptive   = GetBool("GC_OCR_ADAPTIVE", true);
        private static readonly bool DoOpen     = GetBool("GC_OCR_OPEN", true);
        private static readonly bool DoMajority = GetBool("GC_OCR_MAJORITY", true);
        private static readonly bool DoClose    = GetBool("GC_OCR_CLOSE", true);
        private static readonly bool DoSharpen  = GetBool("GC_OCR_SHARPEN", false);
        private static readonly bool DoInvert   = GetBool("GC_OCR_INVERT", false);

        // ---------- New tunables (masks / colors) ----------
        private static readonly double SatColor = GetDouble("GC_OCR_SAT_COLOR", 0.25);
        private static readonly double ValColor = GetDouble("GC_OCR_VAL_COLOR", 0.50);
        private static readonly double SatGray  = GetDouble("GC_OCR_SAT_GRAY",  0.12);
        private static readonly double ValGray  = GetDouble("GC_OCR_VAL_GRAY",  0.85);
// ---------- Binarization ----------
private static readonly int  AdaptWin = ClampOdd(GetInt("GC_OCR_ADAPTIVE_WIN", 31), 3, 199);
// allow negative and positive C
private static readonly int  AdaptC   = Clamp(GetInt("GC_OCR_ADAPTIVE_C", 7), -128, 128);

// Dual-threshold hysteresis (alternative to adaptive/OTSU)
private static readonly bool   DualThr    = GetBool("GC_OCR_DUAL_THR", false);

// Read raw thresholds. Accept either normalized [0..1] or 0..255 integers.
private static readonly double RawThrLow  = GetDouble("GC_OCR_THR_LOW",  0.38);
private static readonly double RawThrHigh = GetDouble("GC_OCR_THR_HIGH", 0.55);

// Normalize: if > 1 assume 0..255 range. Clamp to [0,1].
private static readonly double ThrLow  = RawThrLow  > 1.0 ? Clamp01(RawThrLow  / 255.0) : Clamp01(RawThrLow);
private static readonly double ThrHigh = RawThrHigh > 1.0 ? Clamp01(RawThrHigh / 255.0) : Clamp01(RawThrHigh);

// ---------- Advanced binarizers ----------
// GC_OCR_BINARIZER: "", "sauvola", "wolf" (empty = use existing logic)
private static readonly string BinMethod = (Environment.GetEnvironmentVariable("GC_OCR_BINARIZER") ?? "").Trim().ToLowerInvariant();
// Sauvola params
private static readonly int    SauvolaWin = ClampOdd(GetInt("GC_OCR_SAUVOLA_WIN", 61), 3, 199);
private static readonly double SauvolaK   = GetDouble("GC_OCR_SAUVOLA_K", 0.34);
private static readonly int    SauvolaR   = Clamp(GetInt("GC_OCR_SAUVOLA_R", 128), 1, 255);
// Wolf-Jolion params
private static readonly double WolfK      = GetDouble("GC_OCR_WOLF_K", 0.5);
private static readonly double WolfP      = GetDouble("GC_OCR_WOLF_P", 0.5);

        // ---------- Geometry / scale ----------
        private static readonly double Upscale   = Math.Max(1.0, GetDouble("GC_OCR_UPSCALE", 1.40)); // 1..4 typical

        // ---------- Pre-binarize conditioning ----------
        // GRAYSPACE: "Luma709" (default) or "LabL"
        private static readonly string GraySpace = (Environment.GetEnvironmentVariable("GC_OCR_GRAYSPACE") ?? "Luma709").Trim();
        private static readonly bool   DoCLAHE   = GetBool("GC_OCR_CLAHE", false);
        private static readonly double ClaheClip = Math.Max(0.5, GetDouble("GC_OCR_CLAHE_CLIP", 2.0)); // clip limit
        private static readonly int    ClaheTile = ClampOdd(GetInt("GC_OCR_CLAHE_TILE", 8), 4, 128);   // tile size px
        private static readonly bool   DoPreBlur = GetBool("GC_OCR_PREBLUR", false);
        private static readonly int    PreBlurK  = ClampOdd(GetInt("GC_OCR_PREBLUR_K", 3), 3, 15);
        private static readonly double Gamma     = Math.Max(0.2, Math.Min(5.0, GetDouble("GC_OCR_GAMMA", 1.0))); // 1=no change
        private static readonly double PreContrast = GetDouble("GC_OCR_CONTRAST", 1.0);
        private static readonly int    PreBright   = Clamp(GetInt("GC_OCR_BRIGHT", 0), -100, 100);

        // ---------- Morphology ----------
        private static readonly int MorphK        = ClampOdd(GetInt("GC_OCR_MORPH_K", 3), 3,  nineNine()); // kernel size for erode/dilate/close/open
        private static readonly int OpenIters     = Math.Max(0, GetInt("GC_OCR_OPEN_ITERS",     1));
        private static readonly int MajorityIters = Math.Max(0, GetInt("GC_OCR_MAJORITY_ITERS", 1));
        private static readonly int CloseIters    = Math.Max(0, GetInt("GC_OCR_CLOSE_ITERS",    1));
        // Explicit dilate/erode counts (kept for backward compatibility)
        private static readonly int ExplicitDilate = Math.Max(0, GetInt("GC_OCR_DILATE", 0));
        private static readonly int ExplicitErode  = Math.Max(0, GetInt("GC_OCR_ERODE",  0));

        // Distance thicken (post-binary stroke grow by radius r, r passes)
        private static readonly bool DoDistThicken = GetBool("GC_OCR_DISTANCE_THICKEN", false);
        private static readonly int  DistR         = Math.Max(0, GetInt("GC_OCR_DISTANCE_R", 1));

        // Fill small holes and remove small dots
        private static readonly bool DoFillHoles     = GetBool("GC_OCR_FILL_HOLES", false);
        private static readonly int  FillHolesMax    = Math.Max(1, GetInt("GC_OCR_FILL_HOLES_MAX", 64));
        private static readonly int  RemoveDotsMax   = Math.Max(0, GetInt("GC_OCR_REMOVE_DOTS_MAXAREA", 0));

        private static int _dumpIndex = 0;

        // ---------- Public API ----------
        public static List<string> ReadLines(Bitmap source)
        {
            EnsureEngine();

            using var pre = Preprocess(source);
            if (DebugDump) Dump(pre, "final");

            using var pix = BitmapToPix(pre);
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

                _engine.SetVariable("tessedit_char_whitelist",
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ,:+-()[]'!./");
                _engine.SetVariable("preserve_interword_spaces", "1");
            }
        }

        // ---------- Preprocess pipeline ----------
        private static Bitmap Preprocess(Bitmap input)
        {
            // 1) upscale
            double scale = Upscale;
            int w = Math.Max(1, (int)Math.Round(input.Width * scale));
            int h = Math.Max(1, (int)Math.Round(input.Height * scale));
            var up = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(up))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(input, new Rectangle(0, 0, w, h));
            }
            if (DebugDump) Dump(up, "a_up");

            // 2) tone-map for HDR
            if (ToneMap) { ToneMapAutoInPlace(up); if (DebugDump) Dump(up, "b_tonemap"); }

            // 3) HSV mask
            var mask = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            BuildTextMaskHsv(up, mask, SatColor, ValColor, SatGray, ValGray);
            if (DebugDump) Dump(mask, "c_mask");

            // 4) grayscale from selected colorspace
            var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            if (string.Equals(GraySpace, "LabL", StringComparison.OrdinalIgnoreCase)) ToLabLSafe(up, gray);
            else ToLuma709Safe(up, gray);
            up.Dispose();
            if (DebugDump) Dump(gray, "d_gray");

            // 5) apply mask
            var masked = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            ApplyMaskSafe(gray, mask, masked);
            gray.Dispose();
            mask.Dispose();
            if (DebugDump) Dump(masked, "e_masked");

            // 5.5) CLAHE / gamma / preblur / contrast+bright
            if (DoCLAHE) { var t = new Bitmap(w, h, PixelFormat.Format24bppRgb); ClaheSafe(masked, t, ClaheClip, ClaheTile); masked.Dispose(); masked = t; if (DebugDump) Dump(masked, "e1_clahe"); }
            if (Gamma != 1.0) { var t = new Bitmap(w, h, PixelFormat.Format24bppRgb); ApplyGammaSafe(masked, t, Gamma); masked.Dispose(); masked = t; if (DebugDump) Dump(masked, "e2_gamma"); }
            if (DoPreBlur) { var t = new Bitmap(w, h, PixelFormat.Format24bppRgb); GaussianBlurSafe(masked, t, PreBlurK); masked.Dispose(); masked = t; if (DebugDump) Dump(masked, "e3_blur"); }
            if (Math.Abs(PreContrast - 1.0) > 1e-6 || PreBright != 0)
            {
                var adj = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                AdjustContrastSafe(masked, adj, PreContrast, PreBright);
                masked.Dispose();
                masked = adj;
                if (DebugDump) Dump(masked, "e4_contrast");
            }

            
// 6) binarize
var mono = new Bitmap(w, h, PixelFormat.Format24bppRgb);
if (BinMethod == "sauvola")
{
    SauvolaBinarizeSafe(masked, mono, SauvolaWin, SauvolaK, SauvolaR);}
else if (BinMethod == "wolf")
{
    WolfJolionBinarizeSafe(masked, mono, SauvolaWin, WolfK, WolfP);}
else if (DualThr)
{
    DualThresholdSafe(masked, mono, ThrLow, ThrHigh);}
else if (Adaptive)
{
    AdaptiveMeanBinarizeSafe(masked, mono, AdaptWin, AdaptC);}
else
{
    OtsuBinarizeSafe(masked, mono);}
masked.Dispose();
if (DebugDump) Dump(mono, "f_bin");
// 6.5) invert
            if (DoInvert)
            {
                var inv = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                InvertSafe(mono, inv);
                mono.Dispose();
                mono = inv;
                if (DebugDump) Dump(mono, "f1_invert");
            }

            // 7) open
            if (DoOpen && OpenIters > 0)
            {
                for (int k = 0; k < OpenIters; k++)
                {
                    var opened = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    MorphOpenSafe(mono, opened, MorphK);
                    mono.Dispose();
                    mono = opened;
                }
                if (DebugDump) Dump(mono, "g_open");
            }

            // explicit erode/dilate passes (for backward-compat needed by profiles)
            if (ExplicitErode > 0)
            {
                for (int i = 0; i < ExplicitErode; i++)
                {
                    var t = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    ErodeSafe(mono, t, MorphK);
                    mono.Dispose();
                    mono = t;
                }
            }
            if (ExplicitDilate > 0)
            {
                for (int i = 0; i < ExplicitDilate; i++)
                {
                    var t = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    DilateSafe(mono, t, MorphK);
                    mono.Dispose();
                    mono = t;
                }
            }

            // 8) majority
            if (DoMajority && MajorityIters > 0)
            {
                for (int k = 0; k < MajorityIters; k++)
                {
                    var maj = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    Majority3x3Safe(mono, maj);
                    mono.Dispose();
                    mono = maj;
                }
                if (DebugDump) Dump(mono, "h_majority");
            }

            // 9) close
            if (DoClose && CloseIters > 0)
            {
                for (int k = 0; k < CloseIters; k++)
                {
                    var closed = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    MorphCloseSafe(mono, closed, MorphK);
                    mono.Dispose();
                    mono = closed;
                }
                if (DebugDump) Dump(mono, "i_close");
            }

            // 10) distance thicken (optional substitute for heavy dilate)
            if (DoDistThicken && DistR > 0)
            {
                for (int r = 0; r < DistR; r++)
                {
                    var t = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    DilateSafe(mono, t, 3); // 3x3 grow per radius step
                    mono.Dispose();
                    mono = t;
                }
                if (DebugDump) Dump(mono, "i1_dist");
            }

            // 11) fill small holes (in white foreground)
            if (DoFillHoles && FillHolesMax > 0)
            {
                var t = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                FillHolesSafe(mono, t, FillHolesMax);
                mono.Dispose();
                mono = t;
                if (DebugDump) Dump(mono, "i2_fillholes");
            }

            // 12) remove tiny dots
            if (RemoveDotsMax > 0)
            {
                var t = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                RemoveDotsSafe(mono, t, RemoveDotsMax);
                mono.Dispose();
                mono = t;
                if (DebugDump) Dump(mono, "i3_removedots");
            }

            // 13) sharpen
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

        // ---------- Text splitting ----------
        private static List<string> SplitIntoEntries(string text)
        {
            var results = new List<string>();
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
                int end = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;
                string block = text.Substring(start, end - start);
                block = Regex.Replace(block, @"[ \t]+", " ").Trim();
                if (!string.IsNullOrWhiteSpace(block)) results.Add(block);
            }
            return results;
        }

        private static string NormalizeEntry(string s)
        {
            s = s.Replace("Your,,", "Your ").Replace("Your, ", "Your ").Replace(" ,", " ").Replace("  ", " ");
            s = Regex.Replace(s, @"\bJurret\b", "Turret", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\]([!\.\)]|$)", "$1");
            s = Regex.Replace(s, @"Item Cache\s*at", "Item Cache' at", RegexOptions.IgnoreCase);
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
            catch { }
        }

        // ---------- Image ops (24bpp) ----------
        private static void ToneMapAutoInPlace(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int w = bmp.Width, h = bmp.Height, s = data.Stride;
                var buf = new byte[s * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);

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
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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

        private static void ToLabLSafe(Bitmap src, Bitmap dst)
        {
            // Convert to CIE Lab L* channel (D65). Simple approximate conversion.
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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
                        double b = sb[si + 0] / 255.0;
                        double g = sb[si + 1] / 255.0;
                        double r = sb[si + 2] / 255.0;

                        // sRGB → linear
                        r = SrgbToLinear(r); g = SrgbToLinear(g); b = SrgbToLinear(b);

                        // linear RGB → XYZ (D65)
                        double X = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
                        double Y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
                        double Z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b;

                        // normalize by D65 reference white
                        double Xn = 0.95047, Yn = 1.00000, Zn = 1.08883;
                        double fx = LabF(X / Xn), fy = LabF(Y / Yn), fz = LabF(Z / Zn);

                        double L = 116.0 * fy - 16.0; // 0..100
                        byte L8 = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(L / 100.0 * 255.0)));

                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = L8;
                    }
                }

                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }

            static double SrgbToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            static double LabF(double t) => t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : (7.787 * t + 16.0 / 116.0);
        }

        private static void ApplyMaskSafe(Bitmap gray, Bitmap mask, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, gray.Width, gray.Height);
            var gData = gray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var mData = mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

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

        // ----- Conditioning -----
        private static void ClaheSafe(Bitmap src, Bitmap dst, double clipLimit, int tile)
        {
            // Simple CLAHE per tile, no interpolation. 8-bit grayscale in all 3 channels.
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                for (int y0 = 0; y0 < h; y0 += tile)
                {
                    for (int x0 = 0; x0 < w; x0 += tile)
                    {
                        int y1 = Math.Min(h, y0 + tile);
                        int x1 = Math.Min(w, x0 + tile);

                        int[] hist = new int[256];
                        for (int y = y0; y < y1; y++)
                        {
                            int sr = y * ss;
                            for (int x = x0; x < x1; x++) hist[sb[sr + x * 3]]++;
                        }

                        int count = (x1 - x0) * (y1 - y0);
                        int clip = (int)(clipLimit * count / 256.0);
                        int excess = 0;
                        for (int i = 0; i < 256; i++)
                        {
                            if (hist[i] > clip) { excess += hist[i] - clip; hist[i] = clip; }
                        }
                        int incr = excess / 256;
                        int rem  = excess % 256;
                        for (int i = 0; i < 256; i++) hist[i] += incr;
                        for (int i = 0; i < rem; i++) hist[i]++;

                        int cum = 0;
                        byte[] lut = new byte[256];
                        for (int i = 0; i < 256; i++)
                        {
                            cum += hist[i];
                            lut[i] = (byte)Math.Max(0, Math.Min(255, (cum * 255) / count));
                        }

                        for (int y = y0; y < y1; y++)
                        {
                            int sr = y * ss, dr = y * ds;
                            for (int x = x0; x < x1; x++)
                            {
                                byte v = sb[sr + x * 3];
                                byte vv = lut[v];
                                int di = dr + x * 3;
                                db[di + 0] = db[di + 1] = db[di + 2] = vv;
                            }
                        }
                    }
                }

                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void ApplyGammaSafe(Bitmap src, Bitmap dst, double gamma)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                double inv = 1.0 / gamma;
                byte[] lut = new byte[256];
                for (int i = 0; i < 256; i++)
                {
                    double n = i / 255.0;
                    lut[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Math.Pow(n, inv) * 255.0)));
                }

                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = sb[sr + x * 3];
                        byte g = lut[v];
                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = g;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void GaussianBlurSafe(Bitmap src, Bitmap dst, int k)
        {
            // Separable Gaussian; sigma approx k/3
            k = ClampOdd(k, 3, 31);
            double sigma = k / 3.0;
            double twoSigma2 = 2 * sigma * sigma;
            double[] kernel = new double[k];
            int r = k >> 1;
            double sum = 0;
            for (int i = -r, j = 0; i <= r; i++, j++)
            {
                double v = Math.Exp(-(i * i) / twoSigma2);
                kernel[j] = v; sum += v;
            }
            for (int j = 0; j < k; j++) kernel[j] /= sum;

            // horizontal then vertical
            var tmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            Convolve1DHorizontal(src, tmp, kernel);
            Convolve1DVertical(tmp, dst, kernel);
            tmp.Dispose();
        }

        private static void Convolve1DHorizontal(Bitmap src, Bitmap dst, double[] k)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride, r = k.Length >> 1;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        double acc = 0;
                        for (int i = -r, j = 0; i <= r; i++, j++)
                        {
                            int xx = Math.Max(0, Math.Min(w - 1, x + i));
                            acc += k[j] * sb[sr + xx * 3];
                        }
                        int v = (int)Math.Round(acc);
                        v = Math.Max(0, Math.Min(255, v));
                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = (byte)v;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void Convolve1DVertical(Bitmap src, Bitmap dst, double[] k)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride, r = k.Length >> 1;
                var sb = new byte[ss * h]; var db = new byte[ds * h];
                Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++)
                    {
                        double acc = 0;
                        for (int i = -r, j = 0; i <= r; i++, j++)
                        {
                            int yy = Math.Max(0, Math.Min(h - 1, y + i));
                            acc += k[j] * sb[yy * ss + x * 3];
                        }
                        int v = (int)Math.Round(acc);
                        v = Math.Max(0, Math.Min(255, v));
                        int di = y * ds + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = (byte)v;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        // ----- Binarization -----
        private static void AdaptiveMeanBinarizeSafe(Bitmap srcGray, Bitmap dst, int window, int c)
        {
            window = ClampOdd(window, 3, 199);
            var rect = new Rectangle(0, 0, srcGray.Width, srcGray.Height);
            var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

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
                        int mean = (int)(sum / count);
                        byte pix = sb[sr + x * 3];
                        byte bw = (byte)((pix > (mean - c)) ? 255 : 0);

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
            var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
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

        private static void DualThresholdSafe(Bitmap srcGray, Bitmap dst, double low, double high)
        {
            // Edge-aware hysteresis: strong if >= high, weak if >= low and near strong.
            var rect = new Rectangle(0, 0, srcGray.Width, srcGray.Height);
            var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = srcGray.Width, h = srcGray.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                byte hi = (byte)Math.Round(high * 255.0);
                byte lo = (byte)Math.Round(low  * 255.0);

                byte[] strong = new byte[w * h];
                byte[] outp   = new byte[w * h];

                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, idx = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = sb[sr + x * 3];
                        strong[idx + x] = (byte)(v >= hi ? 1 : 0);
                    }
                }

                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, idx = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = sb[sr + x * 3];
                        bool isStrong = strong[idx + x] == 1;
                        bool isWeak   = v >= lo;

                        byte on = 0;
                        if (isStrong) on = 255;
                        else if (isWeak)
                        {
                            // neighbor of strong?
                            for (int yy = -1; yy <= 1 && on == 0; yy++)
                            {
                                int ny = y + yy; if (ny < 0 || ny >= h) continue;
                                int nidx = ny * w;
                                for (int xx = -1; xx <= 1; xx++)
                                {
                                    int nx = x + xx; if (nx < 0 || nx >= w) continue;
                                    if (strong[nidx + nx] == 1) { on = 255; break; }
                                }
                            }
                        }
                        outp[idx + x] = on;
                    }
                }

                var db = new byte[ds * h];
                for (int y = 0; y < h; y++)
                {
                    int dr = y * ds, idx = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte bw = outp[idx + x];
                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = bw;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { srcGray.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        
private static void SauvolaBinarizeSafe(Bitmap srcGray, Bitmap dst, int window, double k, int R)
{
    window = ClampOdd(window, 3, 199);
    var rect = new Rectangle(0, 0, srcGray.Width, srcGray.Height);
    var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
    var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
    try
    {
        int w = srcGray.Width, h = srcGray.Height, ss = sData.Stride, ds = dData.Stride;
        var sb = new byte[ss * h]; Marshal.Copy(sData.Scan0, sb, 0, sb.Length);
        // integral images for sum and sum of squares
        double[,] integ  = new double[h + 1, w + 1];
        double[,] integ2 = new double[h + 1, w + 1];
        for (int y = 1; y <= h; y++)
        {
            double row = 0, row2 = 0; int sr = (y - 1) * ss;
            for (int x = 1; x <= w; x++)
            {
                byte g = sb[sr + (x - 1) * 3];
                row  += g;
                row2 += g * g;
                integ[y, x]  = integ[y - 1, x] + row;
                integ2[y, x] = integ2[y - 1, x] + row2;
            }
        }
        var db = new byte[ds * h];
        int r = window >> 1; double invR = 1.0 / R;
        for (int y = 0; y < h; y++)
        {
            int dr = y * ds, sr = y * ss;
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
            int A = y0, B = y1 + 1;
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                int C = x0, D = x1 + 1;
                double sum  = integ[B, D]  - integ[A, D]  - integ[B, C]  + integ[A, C];
                double sum2 = integ2[B, D] - integ2[A, D] - integ2[B, C] + integ2[A, C];
                int count = (x1 - x0 + 1) * (y1 - y0 + 1);
                double mean = sum / count;
                double var  = Math.Max(0.0, sum2 / count - mean * mean);
                double std  = Math.Sqrt(var);
                double T = mean * (1.0 + k * ((std * invR) - 1.0));
                byte bw = (byte)((sb[sr + x * 3] > T) ? 255 : 0);
                int di = dr + x * 3;
                db[di + 0] = db[di + 1] = db[di + 2] = bw;
            }
        }
        Marshal.Copy(db, 0, dData.Scan0, db.Length);
    }
    finally { srcGray.UnlockBits(sData); dst.UnlockBits(dData); }
}

private static void WolfJolionBinarizeSafe(Bitmap srcGray, Bitmap dst, int window, double k, double p)
{
    window = ClampOdd(window, 3, 199);
    var rect = new Rectangle(0, 0, srcGray.Width, srcGray.Height);
    var sData = srcGray.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
    var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
    try
    {
        int w = srcGray.Width, h = srcGray.Height, ss = sData.Stride, ds = dData.Stride;
        var sb = new byte[ss * h]; Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

        // integral images for sum and sum of squares and global minimum gray
        double[,] integ  = new double[h + 1, w + 1];
        double[,] integ2 = new double[h + 1, w + 1];
        byte minG = 255;
        for (int y = 1; y <= h; y++)
        {
            double row = 0, row2 = 0; int sr = (y - 1) * ss;
            for (int x = 1; x <= w; x++)
            {
                byte g = sb[sr + (x - 1) * 3];
                if (g < minG) minG = g;
                row  += g;
                row2 += g * g;
                integ[y, x]  = integ[y - 1, x] + row;
                integ2[y, x] = integ2[y - 1, x] + row2;
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
                double sum  = integ[B, D]  - integ[A, D]  - integ[B, C]  + integ[A, C];
                double sum2 = integ2[B, D] - integ2[A, D] - integ2[B, C] + integ2[A, C];
                int count = (x1 - x0 + 1) * (y1 - y0 + 1);
                double mean = sum / count;
                double var  = Math.Max(0.0, sum2 / count - mean * mean);
                double std  = Math.Sqrt(var);

                double T = (1.0 - k) * mean + k * minG + p * std;
                byte bw = (byte)((sb[sr + x * 3] > T) ? 255 : 0);
                int di = dr + x * 3;
                db[di + 0] = db[di + 1] = db[di + 2] = bw;
            }
        }
        Marshal.Copy(db, 0, dData.Scan0, db.Length);
    }
    finally { srcGray.UnlockBits(sData); dst.UnlockBits(dData); }
}
// ----- Morphology with variable kernel -----
        private static void MorphOpenSafe(Bitmap src, Bitmap dst, int k)
        {
            var temp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            ErodeSafe(src, temp, k);
            DilateSafe(temp, dst, k);
            temp.Dispose();
        }

        private static void MorphCloseSafe(Bitmap src, Bitmap dst, int k)
        {
            var temp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            DilateSafe(src, temp, k);
            ErodeSafe(temp, dst, k);
            temp.Dispose();
        }

        private static void ErodeSafe(Bitmap src, Bitmap dst, int k)
        {
            k = ClampOdd(k, 3, nineNine());
            int r = k >> 1;
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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
                        for (int yy = -r; yy <= r && v == 255; yy++)
                        {
                            int ny = y + yy; if (ny < 0 || ny >= h) { v = 0; break; }
                            int nr = ny * ss;
                            for (int xx = -r; xx <= r; xx++)
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

        private static void DilateSafe(Bitmap src, Bitmap dst, int k)
        {
            k = ClampOdd(k, 3, nineNine());
            int r = k >> 1;
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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
                        for (int yy = -r; yy <= r && v == 0; yy++)
                        {
                            int ny = y + yy; if (ny < 0 || ny >= h) continue;
                            int nr = ny * ss;
                            for (int xx = -r; xx <= r; xx++)
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

        // 3×3 majority
        private static void Majority3x3Safe(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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

        // ----- Post-binary cleanup -----
        private static void FillHolesSafe(Bitmap src, Bitmap dst, int maxArea)
        {
            // Fill black holes completely enclosed by white foreground if area ≤ maxArea.
            // Strategy: flood-fill background on inverted image, pixels not reached are holes.
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                // Foreground is 255 (white). Build inverted for background flood.
                byte[] inv = new byte[w * h];
                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, idx = y * w;
                    for (int x = 0; x < w; x++)
                        inv[idx + x] = (byte)(255 - sb[sr + x * 3]);
                }

                // Mark background reachable from borders on inv image.
                bool[] bg = new bool[w * h];
                var qx = new Queue<int>(); var qy = new Queue<int>();
                void Enq(int x, int y)
                {
                    int id = y * w + x; if (bg[id]) return;
                    if (inv[id] == 0) return; // not background in inverted space
                    bg[id] = true; qx.Enqueue(x); qy.Enqueue(y);
                }
                for (int x = 0; x < w; x++) { Enq(x, 0); Enq(x, h - 1); }
                for (int y = 0; y < h; y++) { Enq(0, y); Enq(w - 1, y); }

                while (qx.Count > 0)
                {
                    int x = qx.Dequeue(); int y = qy.Dequeue();
                    for (int yy = -1; yy <= 1; yy++)
                    {
                        int ny = y + yy; if (ny < 0 || ny >= h) continue;
                        for (int xx = -1; xx <= 1; xx++)
                        {
                            int nx = x + xx; if (nx < 0 || nx >= w) continue;
                            int nid = ny * w + nx; if (bg[nid]) continue;
                            if (inv[nid] != 0) { bg[nid] = true; qx.Enqueue(nx); qy.Enqueue(ny); }
                        }
                    }
                }

                // Any remaining inv==1 and bg==false are holes. Flood & fill if area≤maxArea.
                bool[] visited = new bool[w * h];
                byte[] outb = new byte[ds * h];
                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        int id = y * w + x;
                        if (!bg[id] && inv[id] != 0 && !visited[id])
                        {
                            // collect component
                            var comp = new List<int>();
                            var q1 = new Queue<int>(); q1.Enqueue(id); visited[id] = true;
                            while (q1.Count > 0)
                            {
                                int cur = q1.Dequeue(); comp.Add(cur);
                                int cy = cur / w, cx = cur % w;
                                for (int dy = -1; dy <= 1; dy++)
                                {
                                    int ny = cy + dy; if (ny < 0 || ny >= h) continue;
                                    for (int dx = -1; dx <= 1; dx++)
                                    {
                                        int nx = cx + dx; if (nx < 0 || nx >= w) continue;
                                        int nid = ny * w + nx;
                                        if (!visited[nid] && !bg[nid] && inv[nid] != 0)
                                        {
                                            visited[nid] = true; q1.Enqueue(nid);
                                        }
                                    }
                                }
                            }
                            if (comp.Count <= maxArea)
                            {
                                // fill hole -> set foreground white in src space
                                foreach (var p in comp)
                                {
                                    int py = p / w, px = p % w, di = py * ds + px * 3;
                                    outb[di + 0] = outb[di + 1] = outb[di + 2] = 255;
                                }
                            }
                        }
                    }
                }

                // copy existing foreground; overwrite holes we filled
                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        int di = dr + x * 3;
                        byte v = sb[sr + x * 3];
                        if (outb[di] == 0) { outb[di + 0] = outb[di + 1] = outb[di + 2] = v; }
                    }
                }

                Marshal.Copy(outb, 0, dData.Scan0, outb.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void RemoveDotsSafe(Bitmap src, Bitmap dst, int maxArea)
        {
            // Remove white connected components with area ≤ maxArea.
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int w = src.Width, h = src.Height, ss = sData.Stride, ds = dData.Stride;
                var sb = new byte[ss * h]; Marshal.Copy(sData.Scan0, sb, 0, sb.Length);

                bool[] visited = new bool[w * h];
                byte[] outb = new byte[ds * h];

                for (int y = 0; y < h; y++)
                {
                    int sr = y * ss, dr = y * ds;
                    for (int x = 0; x < w; x++)
                    {
                        int id = y * w + x;
                        byte pix = sb[sr + x * 3];
                        if (pix == 0 || visited[id]) continue;

                        // BFS component
                        var comp = new List<int>();
                        var q = new Queue<int>(); q.Enqueue(id); visited[id] = true;
                        while (q.Count > 0)
                        {
                            int cur = q.Dequeue(); comp.Add(cur);
                            int cy = cur / w, cx = cur % w;
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                int ny = cy + dy; if (ny < 0 || ny >= h) continue;
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = cx + dx; if (nx < 0 || nx >= w) continue;
                                    int nid = ny * w + nx;
                                    if (!visited[nid] && sb[ny * ss + nx * 3] != 0)
                                    {
                                        visited[nid] = true; q.Enqueue(nid);
                                    }
                                }
                            }
                        }

                        if (comp.Count > maxArea)
                        {
                            foreach (var p in comp)
                            {
                                int py = p / w, px = p % w, di = py * ds + px * 3;
                                outb[di + 0] = outb[di + 1] = outb[di + 2] = 255;
                            }
                        }
                        // else dropped (removed dot)
                    }
                }

                Marshal.Copy(outb, 0, dData.Scan0, outb.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void Convolve3x3Safe(Bitmap src, Bitmap dst, float[,] k)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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

        private static void AdjustContrastSafe(Bitmap src, Bitmap dst, double contrast, int bright)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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
                        int v = sb[si]; // grayscale in all channels
                        int adj = (int)Math.Round((v - 128) * contrast + 128 + bright);
                        adj = Math.Max(0, Math.Min(255, adj));
                        byte b = (byte)adj;
                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = b;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        private static void InvertSafe(Bitmap src, Bitmap dst)
        {
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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
                        byte val = (byte)(255 - sb[sr + x * 3]);
                        int di = dr + x * 3;
                        db[di + 0] = db[di + 1] = db[di + 2] = val;
                    }
                }
                Marshal.Copy(db, 0, dData.Scan0, db.Length);
            }
            finally { src.UnlockBits(sData); dst.UnlockBits(dData); }
        }

        // ---------- Env helpers ----------
        private static bool GetBool(string key, bool def)
        {
            var s = Environment.GetEnvironmentVariable(key);
            if (s == null) return def;
            return !(s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase));
        }

        private static int GetInt(string key, int def) =>
            int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : def;

        private static double GetDouble(string key, double def) =>
            double.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : def;

        private static int ClampOdd(int v, int min, int max)
        {
            v = Math.Max(min, Math.Min(max, v));
            if ((v & 1) == 0) v++;
            return v;
        }

        private static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
        private static double Clamp01(double d) => d < 0 ? 0 : d > 1 ? 1 : d;
        private static int nineNine() => 99; // keeps morphology kernel sane
    }
}
