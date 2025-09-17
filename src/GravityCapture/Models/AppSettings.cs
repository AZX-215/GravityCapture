using System;
using System.IO;
using System.Text.Json;

namespace GravityCapture.Models
{
    public sealed class AppSettings
    {
        // -------- General (existing) --------
        public string ApiUrl { get; set; } = "";           // (kept for your screenshot API, if used)
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

        // Crop
        public bool   UseCrop { get; set; } = false;
        public double CropX  { get; set; } = 0.0; // normalized (0..1)
        public double CropY  { get; set; } = 0.0;
        public double CropW  { get; set; } = 1.0;
        public double CropH  { get; set; } = 1.0;

        // -------- NEW: Log API per environment --------
        public string LogEnvironment { get; set; } = "Stage"; // "Stage" | "Prod"

        public string LogApiUrlStage { get; set; } = "";   // e.g. https://screenshots-api-stage-production.up.railway.app
        public string LogApiKeyStage { get; set; } = "";

        public string LogApiUrlProd  { get; set; } = "";   // fill if/when you have a prod instance
        public string LogApiKeyProd  { get; set; } = "";

        // Helpers to read/write the active env fields
        public (string url, string key) GetActiveLogApi()
        {
            return LogEnvironment.Equals("Prod", StringComparison.OrdinalIgnoreCase)
                ? (LogApiUrlProd ?? "", LogApiKeyProd ?? "")
                : (LogApiUrlStage ?? "", LogApiKeyStage ?? "");
        }
        public void SetActiveLogApi(string url, string key)
        {
            if (LogEnvironment.Equals("Prod", StringComparison.OrdinalIgnoreCase))
            {
                LogApiUrlProd = url ?? ""; LogApiKeyProd = key ?? "";
            }
            else
            {
                LogApiUrlStage = url ?? ""; LogApiKeyStage = key ?? "";
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
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var obj  = JsonSerializer.Deserialize<AppSettings>(json);
                    if (obj != null)
                    {
                        // defaults if first time
                        if (string.IsNullOrWhiteSpace(obj.LogApiUrlStage))
                            obj.LogApiUrlStage = "https://screenshots-api-stage-production.up.railway.app";
                        return obj;
                    }
                }
            }
            catch { /* ignore and return defaults */ }

            return new AppSettings
            {
                LogApiUrlStage = "https://screenshots-api-stage-production.up.railway.app"
            };
        }

        public void Save()
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsPath, json);
        }
    }
}
