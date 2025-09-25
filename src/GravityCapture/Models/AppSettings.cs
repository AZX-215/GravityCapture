using System;
using System.IO;
using System.Text.Json;

namespace GravityCapture.Models
{
    /// <summary>Matches appsettings.Stage.json and adds a few app-only fields.</summary>
    public sealed partial class AppSettings
    {
        public string LogEnvironment { get; set; } = "Stage";

        // OCR routing
        public bool UseRemoteOcr { get; set; } = true;

        // Preferred modern fields
        public string? ApiBaseUrl { get; set; }
        public AuthSettings? Auth { get; set; }

        // Back-compat (used by some code paths)
        public string? RemoteOcrBaseUrl { get; set; }
        public string? RemoteOcrApiKey  { get; set; }

        // Posting / capture
        public ImageSettings?   Image   { get; set; } = new();
        public CaptureSettings? Capture { get; set; } = new();

        public int    IntervalMinutes { get; set; } = 1;
        public string TribeName       { get; set; } = string.Empty;
        public bool   AutoOcrEnabled  { get; set; } = false;
        public bool   PostOnlyCritical{ get; set; } = false;

        // Crop region normalized to window client rect
        public bool   UseCrop { get; set; } = false;
        public double CropX   { get; set; } = 0;
        public double CropY   { get; set; } = 0;
        public double CropW   { get; set; } = 1;
        public double CropH   { get; set; } = 1;

        public bool Autostart { get; set; } = false;

        public sealed class AuthSettings
        {
            public string? ApiKey { get; set; }
        }

        public sealed class ImageSettings
        {
            public int    JpegQuality          { get; set; } = 90;
            public string ChannelId            { get; set; } = "default";
            public string TargetWindowHint     { get; set; } = "ARK";
            public bool   FilterTameDeath      { get; set; } = true;
            public bool   FilterStructureDestroyed { get; set; } = false;
            public bool   FilterTribeMateDeath { get; set; } = false;
        }

        public sealed class CaptureSettings
        {
            public bool   ActiveWindow { get; set; } = true;
            public string ServerName   { get; set; } = string.Empty;
        }

        // ----- persistence -----
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true
        };

        private static string DefaultPath()
        {
            var dir = AppContext.BaseDirectory;
            var stage = Path.Combine(dir, "appsettings.Stage.json");
            var prod  = Path.Combine(dir, "appsettings.Production.json");
            var def   = Path.Combine(dir, "appsettings.json");
            if (File.Exists(stage)) return stage;
            if (File.Exists(prod))  return prod;
            return def;
        }

        public static AppSettings Load(string? path = null)
        {
            path ??= DefaultPath();
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }

        public void Save(string? path = null)
        {
            path ??= DefaultPath();
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(path, json);
        }
    }
}
