using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GravityCapture.Services;

public sealed class OcrClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _disposeClient;
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    /// <summary>
    /// Create an OCR client.
    /// </summary>
    /// <param name="baseUrl">Base URL of the API, e.g. https://screenshots-api-stage-production.up.railway.app</param>
    /// <param name="apiKey">Optional bearer token.</param>
    /// <param name="httpClient">Optional HttpClient to reuse.</param>
    public OcrClient(string baseUrl, string? apiKey = null, HttpClient? httpClient = null)
    {
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
        _apiKey = apiKey;

        if (httpClient is null)
        {
            _http = new HttpClient();
            _disposeClient = true;
        }
        else
        {
            _http = httpClient;
            _disposeClient = false;
        }
    }

    /// <summary>
    /// Default ctor reads OCR_BASE_URL and OCR_API_KEY from the environment.
    /// Falls back to the stage URL if not set.
    /// </summary>
    public OcrClient()
        : this(
            Environment.GetEnvironmentVariable("OCR_BASE_URL")
                ?? "https://screenshots-api-stage-production.up.railway.app",
            Environment.GetEnvironmentVariable("OCR_API_KEY"),
            null)
    { }

    public async Task<OcrResult> ExtractAsync(string imagePath, string? engine = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("imagePath is required", nameof(imagePath));
        if (!File.Exists(imagePath)) throw new FileNotFoundError(imagePath);

        using var form = new MultipartFormDataContent();

        var stream = File.OpenRead(imagePath);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeTypeFromExtension(imagePath));
        form.Add(fileContent, "file", Path.GetFileName(imagePath));

        if (!string.IsNullOrWhiteSpace(engine))
            form.Add(new StringContent(engine), "engine");

        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/ocr/extract")
        {
            Content = form
        };

        if (!string.IsNullOrEmpty(_apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var resp = await _http.SendAsync(req, ct);
        var payload = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var eng = root.TryGetProperty("engine", out var e) ? (e.GetString() ?? "unknown") : "unknown";
        double conf = 0;
        if (root.TryGetProperty("conf", out var c))
        {
            if (c.ValueKind == JsonValueKind.Number) conf = c.GetDouble();
            else if (c.ValueKind == JsonValueKind.String && double.TryParse(c.GetString(), out var d)) conf = d;
        }

        var lines = new List<OcrLine>();
        if (root.TryGetProperty("lines", out var linesElem) && linesElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in linesElem.EnumerateArray())
            {
                var text = it.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                double lconf = 0;
                if (it.TryGetProperty("conf", out var lc))
                {
                    if (lc.ValueKind == JsonValueKind.Number) lconf = lc.GetDouble();
                    else if (lc.ValueKind == JsonValueKind.String && double.TryParse(lc.GetString(), out var ld)) lconf = ld;
                }

                var bbox = Array.Empty<int>();
                if (it.TryGetProperty("bbox", out var b) && b.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<int>(4);
                    foreach (var n in b.EnumerateArray())
                    {
                        if (n.TryGetInt32(out var v)) list.Add(v);
                    }
                    bbox = list.ToArray();
                }

                lines.Add(new OcrLine(text, lconf, bbox));
            }
        }

        return new OcrResult(eng, conf, lines);
    }

    private static string GetMimeTypeFromExtension(string path)
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

    public void Dispose()
    {
        if (_disposeClient) _http.Dispose();
    }

    // Small helper Exception to give nicer error text.
    private sealed class FileNotFoundError : FileNotFoundException
    {
        public FileNotFoundError(string path) : base($"File not found: {path}", path) { }
    }
}

public sealed record OcrResult(string Engine, double Confidence, IReadOnlyList<OcrLine> Lines);
public sealed record OcrLine(string Text, double Confidence, IReadOnlyList<int> Bbox);
