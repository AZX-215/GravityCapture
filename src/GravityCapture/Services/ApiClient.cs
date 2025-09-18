using System.Net.Http;
using System.Net.Http.Headers;

namespace GravityCapture.Services
{
    public class ApiClient
    {
        private static readonly bool DebugHttp =
            (Environment.GetEnvironmentVariable("GC_DEBUG_HTTP") ?? "0") == "1";

        private readonly HttpClient _http = new HttpClient();
        private readonly string _apiUrl;
        private readonly string _apiKey;

        public ApiClient(string apiUrl, string apiKey)
        {
            _apiUrl = (apiUrl ?? "").TrimEnd('/');
            _apiKey = apiKey ?? "";
        }

        public async Task<bool> SendScreenshotAsync(
            byte[] jpegBytes,
            string fileName,
            ulong channelId,
            string? caption = null)
        {
            if (string.IsNullOrWhiteSpace(_apiUrl))
            {
                Console.WriteLine("[GC] ApiClient: missing API URL.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                Console.WriteLine("[GC] ApiClient: missing API key.");
                return false;
            }

            using var form = new MultipartFormDataContent();

            var file = new ByteArrayContent(jpegBytes);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
            // If your ingest expects Discord-style "files[0]" instead of "file", change the next line.
            form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "capture.jpg" : fileName);

            if (channelId != 0)
                form.Add(new StringContent(channelId.ToString()), "channel_id");

            form.Add(new StringContent(((DateTimeOffset)DateTimeOffset.Now).ToUnixTimeSeconds().ToString()), "ts");

            if (!string.IsNullOrWhiteSpace(caption))
                form.Add(new StringContent(caption), "caption");

            var url = _apiUrl + "/screenshots";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-GL-Key", _apiKey);
            req.Content = form;

            Console.WriteLine($"[HTTP ➜] POST {url}");
            if (DebugHttp)
                Console.WriteLine($"[HTTP ➜] multipart: file=jpg ({jpegBytes?.Length ?? 0} bytes), channel_id={(channelId==0?"<none>":channelId)}, caption={(caption ?? "<none>")}");

            try
            {
                using var res = await _http.SendAsync(req);
                var ok = res.IsSuccessStatusCode;
                Console.WriteLine($"[HTTP ⇦] {(int)res.StatusCode} {res.ReasonPhrase}");

                if (!ok)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    Console.WriteLine($"[HTTP ⇦] error body ({body.Length}): {body}");
                }

                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HTTP ✖] " + ex.Message);
                return false;
            }
        }
    }
}

}
