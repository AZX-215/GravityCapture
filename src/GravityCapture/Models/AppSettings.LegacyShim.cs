using System;

namespace GravityCapture.Models
{
    // Adds missing legacy members referenced by MainWindow and services.
    public partial class AppSettings
    {
        // Environment selector for log ingest UI.
        public string LogEnvironment { get; set; } = "Stage";

        // Ingest API editable fields used by ApiClient.
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";

        // Legacy helpers used by the UI to swap fields.
        public void SetActiveLogApi(string url, string key)
        {
            ApiUrl = url ?? string.Empty;
            ApiKey = key ?? string.Empty;
        }

        public (string? url, string? key) GetActiveLogApi()
        {
            return (ApiUrl, ApiKey);
        }

        // Channel and timing.
        public ulong ChannelId { get; set; } = 0;
        public int IntervalMinutes { get; set; } = 1;
        public bool CaptureActiveWindow { get; set; } = true;
        public int JpegQuality { get; set; } = 90;

        // Targeting and labeling.
        public string TargetWindowHint { get; set; } = "ARK";
        public string ServerName { get; set; } = "";
        public string TribeName { get; set; } = "";

        // Posting filters and modes.
        public bool AutoOcrEnabled { get; set; } = false;
        public bool PostOnlyCritical { get; set; } = false;
        public bool FilterTameDeath { get; set; } = true;
        public bool FilterStructureDestroyed { get; set; } = false;
        public bool FilterTribeMateDeath { get; set; } = false;

        // Crop controls.
        public bool UseCrop { get; set; } = false;
        public double CropX { get; set; } = 0;
        public double CropY { get; set; } = 0;
        public double CropW { get; set; } = 1;
        public double CropH { get; set; } = 1;

        public bool Autostart { get; set; } = false;

        // Remote OCR client config.
        public bool UseRemoteOcr { get; set; } = true;
        public string RemoteOcrBaseUrl { get; set; } = "";
        public string RemoteOcrApiKey { get; set; } = "";
    }
}
