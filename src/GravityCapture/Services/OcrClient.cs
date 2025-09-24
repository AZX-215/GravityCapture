using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GravityCapture.Services
{
    public sealed class OcrClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _baseUrl;
        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public OcrClient(string? baseUrl = null, HttpClient? http = null)
        {
            _http = http ?? new HttpClient();
            _ownsHttp = http is null;

            var env = Environment.GetEnvironmentVariable("OCR_API_BASE");
            _baseUrl = (baseUrl ?? env ?? "https://screenshots-api-stage-production.up.railway.app").TrimEnd('/');
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }

        public async Task<OcrResponse> ExtractFromFileAsync(string filePath, string? engine = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundError(filePath);

            using var form = new MultipartFormDataContent();
            using var fs = File.OpenRead(filePath);
            var content = new StreamContent(fs);
            content.Headers.ContentType = new MediaTypeHeaderValue(DetectContentTypeByExtension(filePath));
            form.Add(content, "file", Path.GetFileName(filePath));

            var url = BuildUrl("/api/ocr/extract", engine);
            using var resp = await _http.PostAsync(url, form, ct).ConfigureAwait(false);
            await EnsureSuccess(resp).ConfigureAwait(false);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var data = await JsonSerializer.DeserializeAsync<OcrResponse>(stream, _json, ct).ConfigureAwait(false);
            return data ?? throw new InvalidOperationException("Empty OCR response.");
        }

        public async Task<OcrResponse> ExtractFromBytesAsync(byte[] bytes, string contentType = "image/png", string? engine = null, CancellationToken ct = default)
        {
            if (bytes is null || bytes.Length == 0) throw new ArgumentException("bytes is empty", nameof(bytes));
            using var body = new ByteArrayContent(bytes);
            body.Headers.ContentType = new MediaTypeHeaderValue(contentType);

            var url = BuildUrl("/api/ocr/extract", engine);
            using var resp = await _http.PostAsync(url, body, ct).ConfigureAwait(false);
            await EnsureSuccess(resp).ConfigureAwait(false);
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var data = await JsonSerializer.DeserializeAsync<OcrResponse>(stream, _json, ct).ConfigureAwait(false);
            return data ?? throw new InvalidOperationException("Empty OCR response.");
        }

        private string BuildUrl(string path, string? engine) =>
            engine is null ? $"{_baseUrl}{path}" : $"{_baseUrl}{path}?engine={Uri.EscapeDataString(engine)}";

        private static string DetectContentTypeByExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };
        }

        private static async Task EnsureSuccess(HttpResponseMessage resp)
        {
            if (resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpRequestException($"OCR API error {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        }
    }

    public sealed class OcrResponse
    {
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "";

        [JsonPropertyName("conf")]
        public double Conf { get; set; }

        [JsonPropertyName("lines")]
        public List<OcrLine> Lines { get; set; } = new();
    }

    public sealed class OcrLine
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("conf")]
        public double Conf { get; set; }

        // [x1, y1, x2, y2]
        [JsonPropertyName("bbox")]
        public int[] Bbox { get; set; } = Array.Empty<int>();
    }

    // Custom exception so callers can catch missing files clearly
    public sealed class FileNotFoundError : FileNotFoundException
    {
        public FileNotFoundError(string? path) : base($"File not found: {path}", path) { }
    }
}
