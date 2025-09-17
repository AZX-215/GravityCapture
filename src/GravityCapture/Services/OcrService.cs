using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Tesseract;

namespace GravityCapture.Services
{
    public static class OcrService
    {
        // If you ever relocate tessdata again, change here:
        private static readonly string TessdataDir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

        /// <summary>
        /// Run OCR on a crop and return cleaned text lines (top-to-bottom).
        /// </summary>
        public static List<string> ReadLines(Bitmap src)
        {
            using var pre = PreprocessForArk(src);         // HDR-friendly preprocessing
            using var pix = PixConverter.ToPix(pre);

            // Tesseract config for multi-line block of uniform text
            using var engine = new TesseractEngine(TessdataDir, "eng", EngineMode.LstmOnly);
            engine.SetVariable("user_defined_dpi", "300");                     // helps accuracy
            engine.SetVariable("preserve_interword_spaces", "1");              // keep spacing
            engine.DefaultPageSegMode = PageSegMode.SingleBlock;              // uniform text block

            using var page = engine.Process(pix);
            var text = page.GetText() ?? string.Empty;

            // Split, clean common OCR confusions
            var lines = text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(CleanLine)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            return lines;
        }

        /// <summary>
        /// Basic cleanup to fix common OCR misreads in ARK logs.
        /// </summary>
        private static string CleanLine(string s)
        {
            var t = s.Trim();

            // Dash and punctuation normalizations
            t = t.Replace('–', '-').Replace('—', '-').Replace('•', '-');
            t = t.Replace('’', '\'').Replace('“', '"').Replace('”', '"');

            // Common character swaps
            t = t.Replace('|', 'l');      // pipe -> lowercase L
            t = t.Replace('O', '0');      // O -> 0 (time/date and levels)
            t = t.Replace("  ", " ");

            // Collapse weird spacing around punctuation
            t = t.Replace(" ,", ",").Replace(" .", ".").Replace(" :", ":").Replace(": ", ": ");

            return t.Trim();
        }

        /// <summary>
        /// HDR-friendly preprocessing: scale up, grayscale, contrast stretch, gentle binarization.
        /// </summary>
        private static Bitmap PreprocessForArk(Bitmap src)
        {
            // 1) Scale up for sharper glyph edges (helps Tesseract)
            var scale = 1.8; // 1.5–2.0 works well for 1080p crops
            var w = (int)Math.Round(src.Width * scale);
            var h = (int)Math.Round(src.Height * scale);
            var scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode      = SmoothingMode.None;
                g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
            }

            // 2) Convert to grayscale + contrast/gamma tweak (counter HDR bloom)
            var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(gray))
            using (var ia = new ImageAttributes())
            {
                // Grayscale matrix
                var grayMatrix = new ColorMatrix(new float[][]
                {
                    new float[] {0.299f, 0.299f, 0.299f, 0, 0},
                    new float[] {0.587f, 0.587f, 0.587f, 0, 0},
                    new float[] {0.114f, 0.114f, 0.114f, 0, 0},
                    new float[] {0,      0,      0,      1, 0},
                    new float[] {0,      0,      0,      0, 1}
                });

                // Contrast/brightness
                const float contrast = 1.35f;   // >1 increases contrast
                const float brightness = 0.00f; // -1..+1 (post-scale offset)
                var t = 0.5f * (1f - contrast) + brightness;

                var contrastMatrix = new ColorMatrix(new float[][]
                {
                    new float[] {contrast, 0,        0,        0, 0},
                    new float[] {0,        contrast, 0,        0, 0},
                    new float[] {0,        0,        contrast, 0, 0},
                    new float[] {0,        0,        0,        1, 0},
                    new float[] {t,        t,        t,        0, 1}
                });

                ia.SetColorMatrix(grayMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(scaled, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);

                ia.ClearColorMatrix();
                ia.SetColorMatrix(contrastMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(gray, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }

            scaled.Dispose();

            // 3) Gentle global threshold – helps separate text from glow
            //    (Not too aggressive; let Tesseract keep some anti-aliased edge info)
            using (var fast = new FastBitmap(gray))
            {
                byte thresh = EstimateOtsuThreshold(fast);
                byte lo = (byte)Math.Max(0, thresh - 10);
                byte hi = (byte)Math.Min(255, thresh + 10);

                fast.ForEachPixel((x, y, ref byte r, ref byte g, ref byte b) =>
                {
                    // simple clamp around threshold window
                    byte v = r; // grayscale already
                    if (v < lo) v = 0;
                    else if (v > hi) v = 255;
                    r = g = b = v;
                });
            }

            return gray;
        }

        /// <summary>Fast bitmap wrapper for raw byte scanning.</summary>
        private sealed class FastBitmap : IDisposable
        {
            private readonly Bitmap _bmp;
            private readonly BitmapData _data;
            private readonly IntPtr _scan0;
            private readonly int _stride;
            private readonly int _bpp;

            public int Width  { get; }
            public int Height { get; }

            public FastBitmap(Bitmap bmp)
            {
                _bmp = bmp;
                Width = bmp.Width; Height = bmp.Height;
                _bpp = Image.GetPixelFormatSize(bmp.PixelFormat) / 8;
                _data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, bmp.PixelFormat);
                _scan0 = _data.Scan0; _stride = _data.Stride;
            }

            public void ForEachPixel(Action<int,int,ref byte,ref byte,ref byte> fn)
            {
                unsafe
                {
                    for (int y = 0; y < Height; y++)
                    {
                        byte* row = (byte*)_scan0 + (y * _stride);
                        for (int x = 0; x < Width; x++)
                        {
                            ref byte b = ref row[x * _bpp + 0];
                            ref byte g = ref row[x * _bpp + 1];
                            ref byte r = ref row[x * _bpp + 2];
                            fn(x, y, ref r, ref g, ref b);
                        }
                    }
                }
            }

            public byte[] GetGrayHistogram()
            {
                var hist = new int[256];
                unsafe
                {
                    for (int y = 0; y < Height; y++)
                    {
                        byte* row = (byte*)_scan0 + (y * _stride);
                        for (int x = 0; x < Width; x++)
                        {
                            byte v = row[x * _bpp + 2]; // R == gray
                            hist[v]++;
                        }
                    }
                }
                // convert to bytes (we only need relative sizes to compute Otsu)
                var outb = new byte[256];
                for (int i = 0; i < 256; i++)
                    outb[i] = (byte)Math.Min(255, hist[i] / Math.Max(1, (Width * Height) / 255));
                return outb;
            }

            public void Dispose() => _bmp.UnlockBits(_data);
        }

        private static byte EstimateOtsuThreshold(FastBitmap fast)
        {
            // Basic Otsu on grayscale histogram
            var hist = new int[256];
            // Expand our byte histogram to int counts
            foreach (var b in fast.GetGrayHistogram())
                hist[b]++;

            int total = hist.Sum();
            long sum = 0;
            for (int t = 0; t < 256; t++)
                sum += t * (long)hist[t];

            long sumB = 0;
            int wB = 0;
            int wF = 0;
            double varMax = 0.0;
            int threshold = 127;

            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                wF = total - wB;
                if (wF == 0) break;

                sumB += t * (long)hist[t];

                double mB = sumB / (double)wB;
                double mF = (sum - sumB) / (double)wF;
                double varBetween = wB * wF * (mB - mF) * (mB - mF);

                if (varBetween > varMax)
                {
                    varMax = varBetween;
                    threshold = t;
                }
            }
            return (byte)threshold;
        }
    }
}
