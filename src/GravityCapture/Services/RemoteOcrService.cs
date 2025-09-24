using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    /// <summary>
    /// Thin wrapper around <see cref="OcrClient"/> that loads settings and
    /// provides a simple file-path based API for the app.
    /// </summary>
    public sealed class RemoteOcrService : IDisposable
    {
        private readonly OcrClient _client;
        private readonly HttpClient _http;

        public RemoteOcrService(AppSettings settings, HttpClient? httpClient = null)
        {
            if (settings is null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl))
                throw new InvalidOperationException("ApiBaseUrl is not configured.");
            if (string.IsNullOrWhiteSpace(settings.Auth?.SharedKey))
                throw new InvalidOperationException("Auth.SharedKey is not configured.");

            _http = httpClient ?? new HttpClient();
            _client = new OcrClient(settings.ApiBaseUrl.TrimEnd('/'), settings.Auth!.SharedKey!, _http);
        }

        /// <summary>
        /// Sends an image file to the stage OCR API and returns the client’s response.
        /// Return type is <c>object</c> to match whatever the current <see cref="OcrClient"/> returns.
        /// (If your OcrClient exposes a concrete type like <c>ExtractResponse</c>, you can swap it here.)
        /// </summary>
        public async Task<object> ExtractAsync(
            string imagePath,
            string contentType = "image/png",
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                throw new ArgumentException("Image path is required.", nameof(imagePath));
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Image not found.", imagePath);

            await using var fs = File.OpenRead(imagePath);
            var fileName = Path.GetFileName(imagePath);

            // Defer to the OcrClient; whatever it returns is passed through.
            var result = await _client.ExtractAsync(fs, fileName, contentType, ct);
            return result!;
        }

        public void Dispose() => _http.Dispose();
    }
}
