using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    public sealed class OcrProfile
    {
        [JsonPropertyName("GC_OCR_TONEMAP")] public int TONEMAP { get; set; } = 0;
        [JsonPropertyName("GC_OCR_ADAPTIVE")] public int ADAPTIVE { get; set; } = 0;
        [JsonPropertyName("GC_OCR_ADAPTIVE_WIN")] public int ADAPTIVE_WIN { get; set; } = 19;
        [JsonPropertyName("GC_OCR_ADAPTIVE_C")] public int ADAPTIVE_C { get; set; } = 0;
        [JsonPropertyName("GC_OCR_SHARPEN")] public int SHARPEN { get; set; } = 0;
        [JsonPropertyName("GC_OCR_OPEN")] public int OPEN { get; set; } = 0;
        [JsonPropertyName("GC_OCR_CLOSE")] public int CLOSE { get; set; } = 0;
        [JsonPropertyName("GC_OCR_DILATE")] public int DILATE { get; set; } = 0;
        [JsonPropertyName("GC_OCR_ERODE")] public int ERODE { get; set; } = 0;
        [JsonPropertyName("GC_OCR_CONTRAST")] public double CONTRAST { get; set; } = 1.0;
        [JsonPropertyName("GC_OCR_INVERT")] public int INVERT { get; set; } = 0;
        [JsonPropertyName("GC_OCR_MAJORITY")] public int MAJORITY { get; set; } = 0;
        [JsonPropertyName("GC_OCR_MAJORITY_ITERS")] public int MAJORITY_ITERS { get; set; } = 0;
        [JsonPropertyName("GC_OCR_OPEN_ITERS")] public int OPEN_ITERS { get; set; } = 0;
        [JsonPropertyName("GC_OCR_UPSCALE")] public int UPSCALE { get; set; } = 1;
    }
}
