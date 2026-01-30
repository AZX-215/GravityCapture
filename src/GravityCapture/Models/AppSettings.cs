using System;

namespace GravityCapture.Models;

public sealed class AppSettings
{
    public string ApiBaseUrl { get; set; } = "http://localhost:8000";
    public string SharedSecret { get; set; } = "";
    public string ServerName { get; set; } = "unknown";
    public string TribeName { get; set; } = "unknown";
    public int IntervalSeconds { get; set; } = 20;
    public bool CriticalPingEnabled { get; set; } = true;
    public bool UseExtractPreview { get; set; } = false;


    // Desktop-side upscaling (2x recommended for OCR). 1..4.
    public int UpscaleFactor { get; set; } = 2;

    // Per-request HTTP timeout for API calls.
    public int RequestTimeoutSeconds { get; set; } = 15;

    // Normalized coordinates (0..1) so the region scales across resolutions.
    public NormalizedRect CaptureRegion { get; set; } = new NormalizedRect();
    public string CaptureScreenDeviceName { get; set; } = ""; // optional for multi-monitor support
}
