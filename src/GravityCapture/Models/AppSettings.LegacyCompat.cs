using System.Collections.Generic;
using System.Drawing;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    public sealed partial class OcrIngestor
    {
        /// <summary>
        /// Map API response to legacy <see cref="OcrLine"/> list.
        /// </summary>
        public static IReadOnlyList<OcrLine> FromExtractResponse(ExtractResponse resp)
        {
            var result = new List<OcrLine>(resp?.Lines?.Count ?? 0);
            if (resp?.Lines == null) return result;

            foreach (var l in resp.Lines)
            {
                Rectangle box = Rectangle.Empty;
                if (l.Bbox is { Length: 4 })
                {
                    int x1 = l.Bbox[0], y1 = l.Bbox[1], x2 = l.Bbox[2], y2 = l.Bbox[3];
                    box = new Rectangle(x1, y1, x2 - x1, y2 - y1);
                }

                result.Add(new OcrLine(l.Text ?? string.Empty, (float)l.Conf, box));
            }

            return result;
        }
    }
}
