#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    public sealed class RemoteOcrService
    {
        private readonly AppSettings _settings;
        private static readonly HttpClient _http = new();

        public RemoteOcrService(AppSettings settings) => _settings = settings;

        private static string Combine(string? root, string path)
            => string.IsNullOrWhiteSpace(root)
                ? path
                : (root!.EndsWith("/") ? root + path.TrimStart('/') : root + "/" + path.TrimStart('/'));

        // -------- Simple OCR (used by OcrIngestor) --------
        public async Task<ExtractResponse> ExtractAsync(Stream png, CancellationToken ct)
        {
            var url = Combine(_settings.ApiBaseUrl, "/extract");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);

            if (!string.IsNullOrWhiteSpace(_settings.Auth?.ApiKey))
                req.Headers.Add("x-api-key", _settings.Auth!.ApiKey);

            var content = new MultipartFormDataContent();
            var img = new StreamContent(png);
            img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(img, "image", "crop.png");
            req.Content = content;

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<ExtractResponse>(json, options);
            return parsed ?? new ExtractResponse { Lines = Array.Empty<ExtractResponse.Line>() };
        }

        // -------- Debug-rich OCR (boxes, binarized PNG) --------
        public sealed class OcrDebugResult
        {
            public string RawJson { get; init; } = "{}";
            public string[] LinesText { get; init; } = Array.Empty<string>();
            public List<Box> Boxes { get; init; } = new();
            public byte[]? BinarizedPng { get; init; }
            public sealed class Box { public double X, Y, W, H, Conf; public string? Text; }
        }

        public async Task<OcrDebugResult> ExtractWithDebugAsync(Stream png, CancellationToken ct)
        {
            var url = Combine(_settings.ApiBaseUrl, "/extract");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);

            if (!string.IsNullOrWhiteSpace(_settings.Auth?.ApiKey))
                req.Headers.Add("x-api-key", _settings.Auth!.ApiKey);

            var content = new MultipartFormDataContent();
            var img = new StreamContent(png);
            img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(img, "image", "crop.png");
            req.Content = content;

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var linesText = new List<string>();
            var boxes = new List<OcrDebugResult.Box>();
            byte[]? bin = null;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (var ln in lines.EnumerateArray())
                {
                    if (ln.TryGetProperty("text", out var tt) && tt.ValueKind == JsonValueKind.String)
                        linesText.Add(tt.GetString() ?? "");

                    var (x, y, w, h) = ReadBox(ln);
                    var conf = ReadDouble(ln, "conf", "confidence");
                    string? text = tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
                    if (w > 0 && h > 0) boxes.Add(new OcrDebugResult.Box { X = x, Y = y, W = w, H = h, Conf = conf, Text = text });
                }
            }

            if (root.TryGetProperty("debug", out var dbg))
            {
                var b64 = ReadString(dbg, "binarized_png", "binarizedPngBase64", "binarized");
                if (!string.IsNullOrWhiteSpace(b64))
                {
                    try { bin = Convert.FromBase64String(b64!); } catch { /* ignore */ }
                }
            }

            return new OcrDebugResult
            {
                RawJson = raw,
                LinesText = linesText.ToArray(),
                Boxes = boxes,
                BinarizedPng = bin
            };
        }

        private static (double x, double y, double w, double h) ReadBox(JsonElement ln)
        {
            if (ln.TryGetProperty("bbox", out var b) && b.ValueKind == JsonValueKind.Object)
                return (ReadDouble(b, "x"), ReadDouble(b, "y"), ReadDouble(b, "w", "width"), ReadDouble(b, "h", "height"));
            if (ln.TryGetProperty("rect", out var r) && r.ValueKind == JsonValueKind.Object)
                return (ReadDouble(r, "left", "x"), ReadDouble(r, "top", "y"), ReadDouble(r, "width", "w"), ReadDouble(r, "height", "h"));
            if (ln.TryGetProperty("box", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 4)
                return (a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble());
            return (0, 0, 0, 0);
        }

        private static double ReadDouble(JsonElement e, params string[] names)
        {
            foreach (var n in names)
                if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number)
                    return v.GetDouble();
            return 0;
        }

        private static string? ReadString(JsonElement e, params string[] names)
        {
            foreach (var n in names)
                if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }
    }
}
