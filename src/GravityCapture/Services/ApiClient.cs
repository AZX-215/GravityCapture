using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace GravityCapture.Services
{
    public class ApiClient
    {
        private readonly HttpClient _http = new HttpClient();
        private readonly string _apiUrl;
        private readonly string _apiKey;

        public ApiClient(string apiUrl, string apiKey)
        {
            _apiUrl = apiUrl.TrimEnd('/');
            _apiKey = apiKey;
        }

        public async Task<bool> SendScreenshotAsync(byte[] jpegBytes, string fileName, ulong channelId, string? caption = null)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(jpegBytes){ Headers = { ContentType = MediaTypeHeaderValue.Parse("image/jpeg") }}, "file", fileName);
            if (channelId != 0) form.Add(new StringContent(channelId.ToString()), "channel_id");
            form.Add(new StringContent(((DateTimeOffset)DateTimeOffset.Now).ToUnixTimeSeconds().ToString()), "ts");
            if (!string.IsNullOrWhiteSpace(caption)) form.Add(new StringContent(caption), "caption");

            var req = new HttpRequestMessage(HttpMethod.Post, _apiUrl + "/screenshots");
            req.Headers.Add("X-GL-Key", _apiKey);
            req.Content = form;

            var res = await _http.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
    }
}
