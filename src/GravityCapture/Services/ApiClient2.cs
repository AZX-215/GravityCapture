using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GravityCapture.Services;

/// <summary>
/// Compatibility shim.
/// Older branches referenced ApiClient2; the current app uses ApiClient.
/// Keep this class minimal so legacy references compile without dragging in outdated settings models.
/// </summary>
public sealed class ApiClient2
{
    private readonly ApiClient _inner;

    public ApiClient2(ApiClient inner)
    {
        _inner = inner;
    }

    public ApiClient2(HttpClient http, string apiBaseUrl, string sharedSecret, string serverName, string tribeName)
    {
        _inner = new ApiClient(http, apiBaseUrl, sharedSecret, serverName, tribeName);
    }

    public Task<ApiEcho?> ExtractAsync(byte[] pngBytes, bool fast = true, int maxW = 1400, CancellationToken ct = default)
        => _inner.ExtractAsync(pngBytes, fast: fast, maxW: maxW, ct: ct);

    public Task<ApiEcho?> IngestScreenshotAsync(byte[] pngBytes, bool previewOnly = false, bool fast = true, int maxW = 1400, CancellationToken ct = default)
        => _inner.IngestScreenshotAsync(pngBytes, previewOnly: previewOnly, fast: fast, maxW: maxW, ct: ct);
}
