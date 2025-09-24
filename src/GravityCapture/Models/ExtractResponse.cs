using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    public sealed class ExtractResponse
    {
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "";

        // average/page confidence
        [JsonPropertyName("conf")]
        public double Conf { get; set; }

        [JsonPropertyName("lines")]
        public List<ExtractLine> Lines { get; set; } = new();
    }

    public sealed class ExtractLine
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("conf")]
        public double Conf { get; set; }

        // [x1, y1, x2, y2]
        [JsonPropertyName("bbox")]
        public int[] Bbox { get; set; } = Array.Empty<int>();
    }
}
