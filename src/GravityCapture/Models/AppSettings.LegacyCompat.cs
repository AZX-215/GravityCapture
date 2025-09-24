using System;

namespace GravityCapture.Models
{
    // Shims to keep older code compiling after the refactor.
    public partial class AppSettings
    {
        // Old name -> new field
        public long TargetWindowHint
        {
            get => TargetWindowHwnd;
            set => TargetWindowHwnd = value;
        }

        // Old boolean used in UI; map to the new flag
        public bool FilterTameDeath
        {
            get => FilterTimeNearDeath;
            set => FilterTimeNearDeath = value;
        }

        // Old API key property; proxy to Remote OCR key
        public string ApiKey
        {
            get => RemoteOcrApiKey;
            set => RemoteOcrApiKey = value;
        }

        // Some screens were calling this; allow a custom URL override.
        public string CustomLogApiUrl { get; set; } = string.Empty;

        public void SetActiveLogApi(string url)
        {
            CustomLogApiUrl = url?.Trim() ?? string.Empty;
            // keep LogEnvironment if callers still use it
        }
    }
}
