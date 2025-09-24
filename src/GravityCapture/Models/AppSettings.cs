using System;
using System.IO;
using System.Text.Json;

namespace GravityCapture.Models
{
    /// <summary>
    /// Back-compat settings model for the Stage app.
    /// </summary>
    public partial class AppSettings
    {
        // ==== General / UI ====
        public bool   Autostart      { get; set; } = false;
        public string ServerName     { get; set; } = "";
        public string TribeName      { get; set; } = "";
        public string LogEnvironment { get; set; } = "stage";

        // ==== Capture ====
        public bool CaptureActiveWindow { get; set; } = true;
        public int  IntervalMinutes     { get; set; } = 1;
        public int  JpegQuality         { get; set; } = 90;
        public long TargetWindowHwnd    { get; set; } = 0;

        // ==== Cropping ====
        public bool UseCrop { get; set; } = false;
        public int  CropX   { get; set; } = 0;
        public int  CropY   { get; set; } = 0;
        public int  CropW   { get; set; } = 0;
        public int  CropH   { get; set; } = 0;

        // ==== OCR / filtering flags ====
        public bool AutoOcrEnabled          { get; set; } = true;
        public bool PostOnlyCritical        { get; set; } = false;
        public bool FilterTimeNearDeath     { get; set; } = false;
        public bool FilterTribeMateDeath    { get; set; } = false;
        public bool FilterStructureDestroyed{ get; set; } = false;

        // ==== Legacy integrations ====
        public string ChannelId  { get; set; } = "";
        public string ApiBaseUrl { get; set; } = "";

        // ==== Remote OCR ====
        public bool   UseRemoteOcr    { get; set; } = true;
        public string RemoteOcrBaseUrl{ get; set; } =
            "https://screenshots-api-stage-production.up.railway.app";
        public string RemoteOcrApiKey { get; set; } = "";

        // ---- Persistence helpers ----
        private const string DefaultFile = "appsettings.Stage.json";

        public static AppSettings Load(string? path = null)
        {
            string file = path ?? Path.Combine(AppContext.BaseDirectory, DefaultFile);
            if (!File.Exists(file)) return new AppSettings();
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<AppSettings>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new AppSettings();
        }

        public void Save(string? path = null)
        {
            string file = path ?? Path.Combine(AppContext.BaseDirectory, DefaultFile);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file, json);
        }

        public string GetActiveLogApi()
        {
            return LogEnvironment?.Equals("prod", StringComparison.OrdinalIgnoreCase) == true
                ? "https://gravity-logs-production.example/api"
                : "https://gravity-logs-stage.example/api";
        }

        public string TrimmedRemoteOcrBase() =>
            (RemoteOcrBaseUrl ?? "").TrimEnd('/');
    }
}
