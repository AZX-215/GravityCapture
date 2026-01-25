using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    // Allow numbers serialized as strings to be read too.
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public sealed class OcrProfile
    {
        // ---- Core knobs ----
        [JsonPropertyName("GC_OCR_TONEMAP")]        public int?    TONEMAP         { get; set; }
        [JsonPropertyName("GC_OCR_ADAPTIVE")]       public int?    ADAPTIVE        { get; set; }
        [JsonPropertyName("GC_OCR_ADAPTIVE_WIN")]   public int?    ADAPTIVE_WIN    { get; set; }
        [JsonPropertyName("GC_OCR_ADAPTIVE_C")]     public int?    ADAPTIVE_C      { get; set; }

        [JsonPropertyName("GC_OCR_SHARPEN")]        public int?    SHARPEN         { get; set; }

        [JsonPropertyName("GC_OCR_OPEN")]           public int?    OPEN            { get; set; }
        [JsonPropertyName("GC_OCR_OPEN_ITERS")]     public int?    OPEN_ITERS      { get; set; }

        [JsonPropertyName("GC_OCR_CLOSE")]          public int?    CLOSE           { get; set; }
        [JsonPropertyName("GC_OCR_CLOSE_ITERS")]    public int?    CLOSE_ITERS     { get; set; }

        [JsonPropertyName("GC_OCR_DILATE")]         public int?    DILATE          { get; set; }
        [JsonPropertyName("GC_OCR_ERODE")]          public int?    ERODE           { get; set; }

        [JsonPropertyName("GC_OCR_CONTRAST")]       public double? CONTRAST        { get; set; }
        [JsonPropertyName("GC_OCR_BRIGHT")]         public double? BRIGHT          { get; set; }

        [JsonPropertyName("GC_OCR_INVERT")]         public int?    INVERT          { get; set; }

        [JsonPropertyName("GC_OCR_MAJORITY")]       public int?    MAJORITY        { get; set; }
        [JsonPropertyName("GC_OCR_MAJORITY_ITERS")] public int?    MAJORITY_ITERS  { get; set; }

        [JsonPropertyName("GC_OCR_UPSCALE")]        public int?    UPSCALE         { get; set; }

        // ---- Capture-stage toggles ----
        [JsonPropertyName("GC_CAPTURE_TONEBOOST")]  public int?    CAPTURE_TONEBOOST { get; set; }

        // ---- SDR helpers ----
        [JsonPropertyName("GC_OCR_PREBLUR")]        public int?    PREBLUR         { get; set; }
        [JsonPropertyName("GC_OCR_PREBLUR_K")]      public int?    PREBLUR_K       { get; set; }

        [JsonPropertyName("GC_OCR_CLAHE")]          public int?    CLAHE           { get; set; }
        [JsonPropertyName("GC_OCR_CLAHE_CLIP")]     public double? CLAHE_CLIP      { get; set; }
        [JsonPropertyName("GC_OCR_CLAHE_TILE")]     public int?    CLAHE_TILE      { get; set; }

        [JsonPropertyName("GC_OCR_GAMMA")]          public double? GAMMA           { get; set; }

        [JsonPropertyName("GC_OCR_MORPH_K")]        public int?    MORPH_K         { get; set; }

        [JsonPropertyName("GC_OCR_DUAL_THR")]       public int?    DUAL_THR        { get; set; }
        [JsonPropertyName("GC_OCR_THR_LOW")]        public int?    THR_LOW         { get; set; }
        [JsonPropertyName("GC_OCR_THR_HIGH")]       public int?    THR_HIGH        { get; set; }

        [JsonPropertyName("GC_OCR_FILL_HOLES")]     public int?    FILL_HOLES      { get; set; }
        [JsonPropertyName("GC_OCR_FILL_HOLES_MAX")] public int?    FILL_HOLES_MAX  { get; set; }

        [JsonPropertyName("GC_OCR_DISTANCE_THICKEN")] public int?    DISTANCE_THICKEN { get; set; }
        [JsonPropertyName("GC_OCR_DISTANCE_R")]       public double? DISTANCE_R       { get; set; }

        [JsonPropertyName("GC_OCR_REMOVE_DOTS_MAXAREA")] public int? REMOVE_DOTS_MAXAREA { get; set; }

        // Gray working space selector
        [JsonPropertyName("GC_OCR_GRAYSPACE")]      public string? GRAYSPACE { get; set; }

        // HSV gating
        [JsonPropertyName("GC_OCR_SAT_COLOR")]      public double? SAT_COLOR       { get; set; }
        [JsonPropertyName("GC_OCR_VAL_COLOR")]      public double? VAL_COLOR       { get; set; }
        [JsonPropertyName("GC_OCR_SAT_GRAY")]       public double? SAT_GRAY        { get; set; }
        [JsonPropertyName("GC_OCR_VAL_GRAY")]       public double? VAL_GRAY        { get; set; }

        // Advanced binarizers
        [JsonPropertyName("GC_OCR_BINARIZER")]      public string? BINARIZER       { get; set; }
        [JsonPropertyName("GC_OCR_SAUVOLA_WIN")]    public int?    SAUVOLA_WIN     { get; set; }
        [JsonPropertyName("GC_OCR_SAUVOLA_K")]      public double? SAUVOLA_K       { get; set; }
        [JsonPropertyName("GC_OCR_SAUVOLA_R")]      public int?    SAUVOLA_R       { get; set; }
        [JsonPropertyName("GC_OCR_WOLF_K")]         public double? WOLF_K          { get; set; }
        [JsonPropertyName("GC_OCR_WOLF_P")]         public double? WOLF_P          { get; set; }

        // Round-trip unknown keys
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
