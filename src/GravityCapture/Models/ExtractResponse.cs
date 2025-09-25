using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    public sealed class ExtractResponse
    {
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = string.Empty;

        [JsonPropertyName("conf")]
        public double Conf { get; set; }

        [JsonPropertyName("lines")]
        public List<ExtractLine> Lines { get; set; } = new();

        /// <summary>Convenience: the OCR’d text as lines.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> TextLines => Lines.ConvertAll(l => l.Text);

        /// <summary>Convenience: the OCR’d text joined by newline.</summary>
        [JsonIgnore]
        public string TextJoined => string.Join(Environment.NewLine, Lines.Select(l => l.Text));
    }

    public sealed class ExtractLine
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("conf")]
        public double Conf { get; set; }

        /// <summary>[x1,y1,x2,y2] in image pixels.</summary>
        [JsonPropertyName("bbox")]
        public int[] Bbox { get; set; } = Array.Empty<int>();

        /// <summary>Make string formatting print the actual OCR text.</summary>
        public override string ToString() => Text ?? string.Empty;
    }
}
