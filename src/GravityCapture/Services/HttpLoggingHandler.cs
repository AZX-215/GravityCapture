using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GravityCapture.Services;

public sealed class HttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger<HttpLoggingHandler> _log;
    private readonly bool _logBodies;

    public HttpLoggingHandler(ILogger<HttpLoggingHandler> log)
    {
        _log = log;
        // Optional toggle via env or config (1 = log bodies)
        _logBodies = (Environment.GetEnvironmentVariable("GC_DEBUG_HTTP") ?? "0") == "1";
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            var reqInfo = new StringBuilder();
            reqInfo.Append($"[HTTP ➜] {req.Method} {req.RequestUri}");
            _log.LogInformation("{msg}", reqInfo.ToString());

            if (_logBodies && req.Content != null)
            {
                var preview = await SafeRead(req.Content);
                _log.LogDebug("[HTTP ➜] Request body ({len}): {body}", preview.Length, preview);
            }

            var resp = await base.SendAsync(req, ct);

            _log.LogInformation("[HTTP ⇦] {code} {reason}", (int)resp.StatusCode, resp.ReasonPhrase);

            if (_logBodies)
            {
                var body = await SafeRead(resp.Content);
                if (!resp.IsSuccessStatusCode)
                    _log.LogWarning("[HTTP ⇦] Error body ({len}): {body}", body.Length, body);
                else
                    _log.LogDebug("[HTTP ⇦] Body ({len}): {body}", body.Length, body);
            }

            return resp;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[HTTP] Exception during request");
            throw;
        }
    }

    private static async Task<string> SafeRead(HttpContent? content)
    {
        if (content == null) return "";
        var bytes = await content.ReadAsByteArrayAsync();
        if (bytes.Length == 0) return "";
        // Don’t blow up logs; trim to 8 KB
        var max = Math.Min(bytes.Length, 8 * 1024);
        return Encoding.UTF8.GetString(bytes, 0, max);
    }
}
