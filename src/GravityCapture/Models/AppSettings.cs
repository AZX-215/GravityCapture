using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    public sealed class AppSettings
    {
        // -------- General (legacy — keep for screenshot API UI, if you still use it) --------
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public ulong  ChannelId { get; set; }
        public int    IntervalMinutes { get; set; } = 1;
        public bool   CaptureActiveWindow { get; set; } = false;
        public int    JpegQuality { get; set; } = 85;
        public string TargetWindowHint { get; set; } = "";
        public bool   Autostart { get; set; } = false;

        // OCR / ingest
        public bool AutoOcrEnabled { get; set; } = false;
        public bool PostOnlyCritical { get; set; } = true;

        // Category filters
        public bool FilterTameDeath { get; set; } = true;
        public bool FilterStructureDestroyed { get; set; } = true;
        public bool FilterTribeMateDeath { get; set; } = true;

        // Crop (normalized)
        public bool   UseCrop { get; set; } = false;
        public double CropX  { get; set; } = 0.0;
        public double CropY  { get; set; } = 0.0;
        public double CropW  { get; set; } = 1.0;
        public double CropH  { get; set; } = 1.0;

        // -------- Log API per environment (this is what LogIngestClient uses) --------
        public string LogEnvironment { get; set; } = "Stage"; // "Stage" | "Prod"

        public string LogApiUrlStage { get; set; } = "";   // e.g. https://screenshots-api-stage-production.up.railway.app
        public string LogApiKeyStage { get; set; } = "";

        public string LogApiUrlProd  { get; set; } = "";   // fill when you have prod ready
        public string LogApiKeyProd  { get; set; } = "";

        /// Helpers to read/write the active env fields
        public (string url, string key) GetActiveLogApi()
        {
            return LogEnvironment.Equals("Prod", StringComparison.OrdinalIgnoreCase)
                ? (LogApiUrlProd ?? "",  LogApiKeyProd  ?? "")
                : (LogApiUrlStage ?? "", LogApiKeyStage ?? "");
        }
        public void SetActiveLogApi(string url, string key)
        {
            if (LogEnvironment.Equals("Prod", StringComparison.OrdinalIgnoreCase))
            {
                LogApiUrlProd  = url ?? "";
                LogApiKeyProd  = key ?? "";
            }
            else
            {
                LogApiUrlStage = url ?? "";
                LogApiKeyStage = key ?? "";
            }
        }

        // Convenience properties for UI binding (not serialized)
        [JsonIgnore]
        public string ActiveLogApiUrl
        {
            get => GetActiveLogApi().url;
            set
            {
                var (_, key) = GetActiveLogApi();
                SetActiveLogApi(value ?? "", key ?? "");
            }
        }
        [JsonIgnore]
        public string ActiveLogApiKey
        {
            get => GetActiveLogApi().key;
            set
            {
                var (url, _) = GetActiveLogApi();
                SetActiveLogApi(url ?? "", value ?? "");
            }
        }

        // -------- persistence --------
        private static string SettingsPath
        {
            get
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GravityCapture");
                Directory.CreateDirectory(root);
                return Path.Combine(root, "global.json");
            }
        }

        public static AppSettings Load()
        {
            AppSettings obj = new AppSettings();

            // 1) Load file if present
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var fromDisk = JsonSerializer.Deserialize<AppSettings>(json);
                    if (fromDisk != null) obj = fromDisk;
                }
            }
            catch
            {
                // ignore and continue with defaults/auto-import
            }

            // 2) Auto-import Stage defaults on first run / if blank
            TryImportStageDefaults(obj);

            // 3) Ensure sensible default for Stage URL if still empty
            if (string.IsNullOrWhiteSpace(obj.LogApiUrlStage))
                obj.LogApiUrlStage = "https://screenshots-api-stage-production.up.railway.app";

            // 4) Honor GC_ENV override ("Stage" | "Prod")
            var gcEnv = Environment.GetEnvironmentVariable("GC_ENV");
            if (!string.IsNullOrWhiteSpace(gcEnv))
                obj.LogEnvironment = gcEnv;

            return obj;
        }

        public void Save()
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }

        private static void TryImportStageDefaults(AppSettings obj)
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var stagePath = Path.Combine(baseDir, "appsettings.Stage.json");
                if (!File.Exists(stagePath)) return;

                using var doc = JsonDocument.Parse(File.ReadAllText(stagePath));
                if (doc.RootElement.TryGetProperty("ApiBaseUrl", out var urlEl))
                {
                    if (string.IsNullOrWhiteSpace(obj.LogApiUrlStage))
                        obj.LogApiUrlStage = urlEl.GetString() ?? "";
                }
                if (doc.RootElement.TryGetProperty("Auth", out var authEl) &&
                    authEl.TryGetProperty("SharedKey", out var keyEl))
                {
                    if (string.IsNullOrWhiteSpace(obj.LogApiKeyStage))
                        obj.LogApiKeyStage = keyEl.GetString() ?? "";
                }
            }
            catch { /* best effort */ }
        }
    }
}
