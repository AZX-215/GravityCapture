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

    // Normalized coordinates (0..1) so the region scales across resolutions.
    public NormalizedRect CaptureRegion { get; set; } = new NormalizedRect();
    public string CaptureScreenDeviceName { get; set; } = ""; // optional for multi-monitor support
}
