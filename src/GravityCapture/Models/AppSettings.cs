using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GravityCapture
{
    public sealed class AppSettings
    {
        // ---------- storage ----------
        private static readonly string RootDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GravityCapture");

        private static readonly string SettingsPath = Path.Combine(RootDir, "global.json");

        // ---------- environment / API ----------
        public string LogEnvironment { get; set; } = "Stage"; // "Stage" | "Prod"

        // Stage API
        public string? StageApiUrl { get; set; }
        public string? StageApiKey { get; set; }

        // Prod API
        public string? ProdApiUrl { get; set; }
        public string? ProdApiKey { get; set; }

        // For convenience (legacy callers)
        [JsonIgnore] public string ApiUrl => (GetActiveLogApi().url ?? string.Empty);
        [JsonIgnore] public string ApiKey => (GetActiveLogApi().key ?? string.Empty);

        public (string? url, string? key) GetActiveLogApi()
            => string.Equals(LogEnvironment, "Prod", StringComparison.OrdinalIgnoreCase)
               ? (ProdApiUrl, ProdApiKey)
               : (StageApiUrl, StageApiKey);

        public void SetActiveLogApi(string url, string key)
        {
            if (string.Equals(LogEnvironment, "Prod", StringComparison.OrdinalIgnoreCase))
            {
                ProdApiUrl = url;
                ProdApiKey = key;
            }
            else
            {
                StageApiUrl = url;
                StageApiKey = key;
            }
        }

        // ---------- discord / posting ----------
        public ulong ChannelId { get; set; }
        public int IntervalMinutes { get; set; } = 1;

        // ---------- capture / crop ----------
        public bool CaptureActiveWindow { get; set; } = true;
        public int JpegQuality { get; set; } = 85;

        public bool UseCrop { get; set; } = false;
        public double CropX { get; set; }
        public double CropY { get; set; }
        public double CropW { get; set; }
        public double CropH { get; set; }

        // (Not used anymore for you, but kept for compatibility)
        public string? TargetWindowHint { get; set; } = string.Empty;

        // ---------- OCR / posting logic ----------
        public bool AutoOcrEnabled { get; set; } = true;
        public bool PostOnlyCritical { get; set; } = false;

        public bool FilterTameDeath { get; set; } = false;
        public bool FilterStructureDestroyed { get; set; } = false;
        public bool FilterTribeMateDeath { get; set; } = false;

        public bool Autostart { get; set; } = false;

        // ---------- NEW: persist Server/Tribe ----------
        public string? ServerName { get; set; } = string.Empty;
        public string? TribeName  { get; set; } = string.Empty;

        // ---------- load/save ----------
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions()) ?? new AppSettings();

                    // Back-compat defaulting
                    s.LogEnvironment = string.IsNullOrWhiteSpace(s.LogEnvironment) ? "Stage" : s.LogEnvironment;
                    s.IntervalMinutes = Math.Max(1, s.IntervalMinutes <= 0 ? 1 : s.IntervalMinutes);
                    s.JpegQuality = s.JpegQuality is < 1 or > 100 ? 85 : s.JpegQuality;

                    return s;
                }
            }
            catch { /* ignore and fall through to defaults */ }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(RootDir);
                var json = JsonSerializer.Serialize(this, JsonOptions());
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // swallow – UI shows status messages already
            }
        }

        private static JsonSerializerOptions JsonOptions() => new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}
