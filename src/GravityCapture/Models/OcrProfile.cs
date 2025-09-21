using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    // Allow numbers serialized as strings to be read too.
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public sealed class OcrProfile
    {
        // ---- Core knobs (existing) ----
        [JsonPropertyName("GC_OCR_TONEMAP")]        public int    TONEMAP         { get; set; } = 0;
        [JsonPropertyName("GC_OCR_ADAPTIVE")]       public int    ADAPTIVE        { get; set; } = 1;
        [JsonPropertyName("GC_OCR_ADAPTIVE_WIN")]   public int    ADAPTIVE_WIN    { get; set; } = 19;
        [JsonPropertyName("GC_OCR_ADAPTIVE_C")]     public int    ADAPTIVE_C      { get; set; } = 0;

        [JsonPropertyName("GC_OCR_SHARPEN")]        public int    SHARPEN         { get; set; } = 1;

        [JsonPropertyName("GC_OCR_OPEN")]           public int    OPEN            { get; set; } = 0;
        [JsonPropertyName("GC_OCR_OPEN_ITERS")]     public int    OPEN_ITERS      { get; set; } = 0;

        [JsonPropertyName("GC_OCR_CLOSE")]          public int    CLOSE           { get; set; } = 0;
        [JsonPropertyName("GC_OCR_CLOSE_ITERS")]    public int    CLOSE_ITERS     { get; set; } = 0;

        [JsonPropertyName("GC_OCR_DILATE")]         public int    DILATE          { get; set; } = 0;
        [JsonPropertyName("GC_OCR_ERODE")]          public int    ERODE           { get; set; } = 0;

        [JsonPropertyName("GC_OCR_CONTRAST")]       public double CONTRAST        { get; set; } = 1.0;
        [JsonPropertyName("GC_OCR_BRIGHT")]         public double BRIGHT          { get; set; } = 0.0;

        [JsonPropertyName("GC_OCR_INVERT")]         public int    INVERT          { get; set; } = 0;

        [JsonPropertyName("GC_OCR_MAJORITY")]       public int    MAJORITY        { get; set; } = 0;
        [JsonPropertyName("GC_OCR_MAJORITY_ITERS")] public int    MAJORITY_ITERS  { get; set; } = 0;

        [JsonPropertyName("GC_OCR_UPSCALE")]        public int    UPSCALE         { get; set; } = 2;

        // ---- New SDR helpers (optional, safe defaults) ----
        // Pre-blur before thresholding
        [JsonPropertyName("GC_OCR_PREBLUR")]        public int    PREBLUR         { get; set; } = 0;   // 0/1
        [JsonPropertyName("GC_OCR_PREBLUR_K")]      public int    PREBLUR_K       { get; set; } = 3;   // odd kernel

        // CLAHE local contrast
        [JsonPropertyName("GC_OCR_CLAHE")]          public int    CLAHE           { get; set; } = 0;   // 0/1
        [JsonPropertyName("GC_OCR_CLAHE_CLIP")]     public double CLAHE_CLIP      { get; set; } = 2.0;
        [JsonPropertyName("GC_OCR_CLAHE_TILE")]     public int    CLAHE_TILE      { get; set; } = 8;   // tile size

        // Gamma curve (applied in gray)
        [JsonPropertyName("GC_OCR_GAMMA")]          public double GAMMA           { get; set; } = 1.0;

        // Morphology kernel override
        [JsonPropertyName("GC_OCR_MORPH_K")]        public int    MORPH_K         { get; set; } = 3;   // odd kernel

        // Dual-threshold mode
        [JsonPropertyName("GC_OCR_DUAL_THR")]       public int    DUAL_THR        { get; set; } = 0;   // 0/1
        [JsonPropertyName("GC_OCR_THR_LOW")]        public int    THR_LOW         { get; set; } = 120; // 0..255 (normalized in service)
        [JsonPropertyName("GC_OCR_THR_HIGH")]       public int    THR_HIGH        { get; set; } = 180; // 0..255 (normalized in service)

        // Fill internal holes in components
        [JsonPropertyName("GC_OCR_FILL_HOLES")]     public int    FILL_HOLES      { get; set; } = 0;   // 0/1
        [JsonPropertyName("GC_OCR_FILL_HOLES_MAX")] public int    FILL_HOLES_MAX  { get; set; } = 400; // px area

        // Distance-transform based thickening
        [JsonPropertyName("GC_OCR_DISTANCE_THICKEN")] public int    DISTANCE_THICKEN { get; set; } = 0;  // 0/1
        [JsonPropertyName("GC_OCR_DISTANCE_R")]       public double DISTANCE_R       { get; set; } = 1.0;  // e.g., 1 or 1.4

        // Remove small dot noise
        [JsonPropertyName("GC_OCR_REMOVE_DOTS_MAXAREA")] public int REMOVE_DOTS_MAXAREA { get; set; } = 0;

        // Gray working space selector
        [JsonPropertyName("GC_OCR_GRAYSPACE")]      public string GRAYSPACE { get; set; } = "Luma709";

        // HSV gating to preserve gray vs colored text
        [JsonPropertyName("GC_OCR_SAT_COLOR")]      public double SAT_COLOR       { get; set; } = 0.25;
        [JsonPropertyName("GC_OCR_VAL_COLOR")]      public double VAL_COLOR       { get; set; } = 0.50;
        [JsonPropertyName("GC_OCR_SAT_GRAY")]       public double SAT_GRAY        { get; set; } = 0.12;
        [JsonPropertyName("GC_OCR_VAL_GRAY")]       public double VAL_GRAY        { get; set; } = 0.85;

        // ---- Advanced binarizers (new) ----
        // "", "sauvola", or "wolf". Empty means "use legacy pipeline".
        [JsonPropertyName("GC_OCR_BINARIZER")]      public string BINARIZER       { get; set; } = "";

        // Sauvola parameters
        [JsonPropertyName("GC_OCR_SAUVOLA_WIN")]    public int    SAUVOLA_WIN     { get; set; } = 61;
        [JsonPropertyName("GC_OCR_SAUVOLA_K")]      public double SAUVOLA_K       { get; set; } = 0.34;
        [JsonPropertyName("GC_OCR_SAUVOLA_R")]      public int    SAUVOLA_R       { get; set; } = 128;

        // Wolf–Jolion parameters
        [JsonPropertyName("GC_OCR_WOLF_K")]         public double WOLF_K          { get; set; } = 0.5;
        [JsonPropertyName("GC_OCR_WOLF_P")]         public double WOLF_P          { get; set; } = 0.5;

        // ---- Future-proof: round-trip unknown keys instead of dropping them ----
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
