using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    public sealed class RemoteOcrService
    {
        private readonly AppSettings _settings;
        private static readonly HttpClient _http = new HttpClient();

        public RemoteOcrService(AppSettings settings) => _settings = settings;

        public sealed class OcrDebugResult
        {
            public string[] LinesText { get; init; } = Array.Empty<string>();
            public List<Box> Boxes { get; init; } = new();
            public string RawJson { get; init; } = "{}";
            public byte[]? BinarizedPng { get; init; }
            public sealed class Box { public double X, Y, W, H, Conf; public string? Text; }
        }

        // New: returns raw JSON, per-line boxes, and optional binarized image
        public async Task<OcrDebugResult> ExtractWithDebugAsync(Stream pngStream, CancellationToken ct)
        {
            var url = Combine(_settings.ApiBaseUrl, "/extract");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(_settings.Auth?.ApiKey))
                req.Headers.Add("x-api-key", _settings.Auth.ApiKey);

            using var content = new MultipartFormDataContent();
            var img = new StreamContent(pngStream);
            img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(img, "image", "crop.png");

            AddCommonFlags(content);

            req.Content = content;
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            resp.EnsureSuccessStatusCode();
            var raw = await resp.Content.ReadAsStringAsync(ct);

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

                    double conf = TryGetDouble(ln, "conf", "confidence");
                    // bbox: {x,y,w,h} or {left,top,width,height} or [x,y,w,h]
                    (double x, double y, double w, double h) = TryGetBox(ln);
                    string? text = tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
                    if (w > 0 && h > 0)
                        boxes.Add(new OcrDebugResult.Box { X = x, Y = y, W = w, H = h, Conf = conf, Text = text });
                }
            }

            if (root.TryGetProperty("debug", out var dbg))
            {
                // support common field names
                var b64 = GetFirstString(dbg, "binarized_png", "binarizedPngBase64", "binarized");
                if (!string.IsNullOrWhiteSpace(b64))
                {
                    try { bin = Convert.FromBase64String(b64!); } catch { }
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

        private static (double x, double y, double w, double h) TryGetBox(JsonElement ln)
        {
            if (ln.TryGetProperty("bbox", out var b) && b.ValueKind == JsonValueKind.Object)
                return (TryGetDouble(b, "x"), TryGetDouble(b, "y"), TryGetDouble(b, "w", "width"), TryGetDouble(b, "h", "height"));
            if (ln.TryGetProperty("rect", out var r) && r.ValueKind == JsonValueKind.Object)
                return (TryGetDouble(r, "left", "x"), TryGetDouble(r, "top", "y"), TryGetDouble(r, "width", "w"), TryGetDouble(r, "height", "h"));
            if (ln.TryGetProperty("box", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() >= 4)
                return (arr[0].GetDouble(), arr[1].GetDouble(), arr[2].GetDouble(), arr[3].GetDouble());
            return (0, 0, 0, 0);
        }

        private static double TryGetDouble(JsonElement obj, params string[] names)
        {
            foreach (var n in names)
                if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number)
                    return v.GetDouble();
            return 0;
        }

        private static string? GetFirstString(JsonElement obj, params string[] names)
        {
            foreach (var n in names)
                if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }

        private static void AddCommonFlags(MultipartFormDataContent form)
        {
            // include filters so server can mirror UI intent
            void Add(string name, string? val)
            {
                if (val == null) return;
                form.Add(new StringContent(val), name);
            }
            // add more flags as needed
        }

        private static string Combine(string? a, string b)
        {
            if (string.IsNullOrWhiteSpace(a)) return b;
            if (a!.EndsWith("/")) return a + b.TrimStart('/');
            return a + "/" + b.TrimStart('/');
        }
    }
}
