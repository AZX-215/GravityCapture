using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GravityCapture.Services
{
    public sealed class OcrClient
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _engine;

        public OcrClient(string baseUrl, string engine = "tess", HttpClient? http = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _engine  = string.IsNullOrWhiteSpace(engine) ? "tess" : engine;
            _http    = http ?? new HttpClient();
        }

        public async Task<OcrResult> ExtractFromFileAsync(string filePath, string? engine = null, CancellationToken ct = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Image not found", filePath);

            var url = $"{_baseUrl}/api/ocr/extract?engine={(engine ?? _engine)}";
            using var fs   = File.OpenRead(filePath);
            using var cnt  = new StreamContent(fs);
            cnt.Headers.ContentType = new MediaTypeHeaderValue(GetContentTypeFromPath(filePath)); // ensure image/*

            using var mp   = new MultipartFormDataContent();
            mp.Add(cnt, "file", Path.GetFileName(filePath)); // API accepts 'file' or 'image'

            using var resp = await _http.PostAsync(url, mp, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<OcrResult>(json, JsonOpts)
                         ?? throw new InvalidOperationException("Empty OCR response");
            return result;
        }

        public async Task<OcrResult> ExtractFromBytesAsync(byte[] bytes, string fileName = "image.png", string? engine = null, CancellationToken ct = default)
        {
            var url = $"{_baseUrl}/api/ocr/extract?engine={(engine ?? _engine)}";
            using var cnt = new ByteArrayContent(bytes);
            cnt.Headers.ContentType = new MediaTypeHeaderValue(GetContentTypeFromPath(fileName));

            using var mp = new MultipartFormDataContent();
            mp.Add(cnt, "file", fileName);

            using var resp = await _http.PostAsync(url, mp, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<OcrResult>(json, JsonOpts)!;
        }

        private static string GetContentTypeFromPath(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".png"  => "image/png",
                ".jpg"  => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp"  => "image/bmp",
                _       => "image/png"
            };
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public sealed class OcrResult
    {
        [JsonPropertyName("engine")] public string Engine { get; set; } = "";
        [JsonPropertyName("conf")]   public double Conf { get; set; }
        [JsonPropertyName("lines")]  public List<OcrLine> Lines { get; set; } = new();
    }

    public sealed class OcrLine
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("conf")] public double Conf { get; set; }
        [JsonPropertyName("bbox")] public int[] Bbox { get; set; } = Array.Empty<int>();
    }
}
