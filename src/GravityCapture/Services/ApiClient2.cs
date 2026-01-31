using System.Threading;
using System.Threading.Tasks;
using GravityCapture.Models;

namespace GravityCapture.Services;

/// <summary>
/// Compatibility wrapper for legacy references to ApiClient2.
/// Current app logic uses ApiClient which returns JSON strings from the API.
/// This wrapper intentionally exposes the same "string response" contract and
/// does not depend on any removed DTO types (e.g., ApiEcho).
/// </summary>
public sealed class ApiClient2
{
    private readonly ApiClient _inner;

    public ApiClient2()
    {
        _inner = new ApiClient();
    }

    public ApiClient2(ApiClient inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Calls /extract (preview) and returns the raw JSON response string.
    /// </summary>
    public Task<string> ExtractAsync(
        byte[] imageBytes,
        string contentType,
        string fileName,
        AppSettings settings,
        CancellationToken ct,
        bool fast = true)
    {
        return _inner.ExtractAsync(imageBytes, contentType, fileName, settings, ct, fast: fast);
    }

    /// <summary>
    /// Calls /ingest/screenshot and returns the raw JSON response string.
    /// </summary>
    public Task<string> SendIngestScreenshotAsync(
        byte[] imageBytes,
        string contentType,
        string fileName,
        AppSettings settings,
        CancellationToken ct)
    {
        return _inner.SendIngestScreenshotAsync(imageBytes, contentType, fileName, settings, ct);
    }
}
