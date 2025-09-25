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
        private readonly string _base;
        private readonly string _apiKey;

        public RemoteOcrService(AppSettings settings)
        {
            _base   = (settings.ApiBaseUrl ?? "").TrimEnd('/');
            _apiKey = settings.Auth?.ApiKey ?? "";
        }

        public async Task<ExtractResponse> ExtractAsync(Stream imageStream, CancellationToken ct)
        {
            using var http = new HttpClient();
            if (!string.IsNullOrEmpty(_apiKey))
                http.DefaultRequestHeaders.Add("ApiKey", _apiKey);

            using var form = new MultipartFormDataContent();
            var sc = new StreamContent(imageStream);
            sc.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(sc, "file", "capture.png");

            var resp = await http.PostAsync($"{_base}/api/ocr/extract", form, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var obj = JsonSerializer.Deserialize<ExtractResponse>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ExtractResponse();

            obj.Lines ??= new();
            return obj;
        }
    }
}
