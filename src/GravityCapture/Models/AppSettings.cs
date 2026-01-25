using System;
using System.IO;
using System.Text.Json;

namespace GravityCapture.Models
{
    public partial class AppSettings
    {
        public string? ApiBaseUrl { get; set; }

        public AuthSettings Auth { get; set; } = new();
        public ImageSettings Image { get; set; } = new();
        public CaptureSettings Capture { get; set; } = new();
        public FilterSettings Filters { get; set; } = new();

        // Legacy carryover
        public string? ServerName { get; set; }
        public string? TribeName { get; set; }

        // Capture rectangle
        public bool UseCrop { get; set; }
        public int CropX { get; set; }
        public int CropY { get; set; }
        public int CropW { get; set; }
        public int CropH { get; set; }

        // UI behavior
        public int IntervalMinutes { get; set; } = 0;

        // Optional endpoint overrides
        public string? OcrPath { get; set; }         // e.g. "/ocr" or "/api/extract"
        public string? ScreenshotIngestPath { get; set; } = "/ingest/screenshot";
        public string? LogLineIngestPath { get; set; } = "/ingest/log-line";

        public sealed class AuthSettings
        {
            public string? ApiKey { get; set; }
            public string? SharedKey { get; set; } // legacy
        }

        public sealed class ImageSettings
        {
            public string? ChannelId { get; set; }
            public int JpegQuality { get; set; } = 100;
            public string? TargetWindowHint { get; set; } = "ARK";
        }

        public sealed class CaptureSettings
        {
            public bool ActiveWindow { get; set; } = true;
            public string? ServerName { get; set; }
            public string? TargetWindowHint { get; set; }
        }

        public sealed class FilterSettings
        {
            public bool TameDeaths { get; set; }
            public bool TamesStarved { get; set; }
            public bool StructuresDestroyed { get; set; }
            public bool StructuresAutoDecay { get; set; }
            public bool TribemateDeaths { get; set; }
            public bool TribeKillsEnemyTames { get; set; }
            public bool EnemyPlayerKills { get; set; }
            public bool TribematesDemolishing { get; set; }
            public bool TribematesFreezingTames { get; set; }
        }

        // -------- persistence --------
        private static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GravityCapture");
        private static string GlobalPath => Path.Combine(ConfigDir, "global.json");
        private static string StageSeedPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "appsettings.Stage.json");

        private static readonly JsonSerializerOptions J = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            AppSettings r = new();
            try
            {
                if (File.Exists(StageSeedPath))
                {
                    var seeded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StageSeedPath));
                    if (seeded != null) r = Merge(r, seeded);
                }
                if (File.Exists(GlobalPath))
                {
                    var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(GlobalPath));
                    if (persisted != null) r = Merge(r, persisted);
                }
            }
            catch { }
            Migrate(r);
            return r;
        }

        public void Save()
        {
            Migrate(this);
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(GlobalPath, JsonSerializer.Serialize(this, J));
        }

        private static AppSettings Merge(AppSettings a, AppSettings b)
        {
            a.ApiBaseUrl = string.IsNullOrWhiteSpace(b.ApiBaseUrl) ? a.ApiBaseUrl : b.ApiBaseUrl;

            a.Auth ??= new AuthSettings();
            b.Auth ??= new AuthSettings();
            a.Auth.ApiKey = string.IsNullOrWhiteSpace(b.Auth.ApiKey) ? a.Auth.ApiKey : b.Auth.ApiKey;
            a.Auth.SharedKey = string.IsNullOrWhiteSpace(b.Auth.SharedKey) ? a.Auth.SharedKey : b.Auth.SharedKey;

            a.Image ??= new ImageSettings();
            b.Image ??= new ImageSettings();
            a.Image.ChannelId = string.IsNullOrWhiteSpace(b.Image.ChannelId) ? a.Image.ChannelId : b.Image.ChannelId;
            a.Image.JpegQuality = b.Image.JpegQuality != 0 ? b.Image.JpegQuality : a.Image.JpegQuality;
            a.Image.TargetWindowHint = string.IsNullOrWhiteSpace(b.Image.TargetWindowHint) ? a.Image.TargetWindowHint : b.Image.TargetWindowHint;

            a.Capture ??= new CaptureSettings();
            b.Capture ??= new CaptureSettings();
            a.Capture.ActiveWindow |= b.Capture.ActiveWindow;
            a.Capture.ServerName = string.IsNullOrWhiteSpace(b.Capture.ServerName) ? a.Capture.ServerName : b.Capture.ServerName;
            a.Capture.TargetWindowHint = string.IsNullOrWhiteSpace(b.Capture.TargetWindowHint) ? a.Capture.TargetWindowHint : b.Capture.TargetWindowHint;

            a.Filters ??= new FilterSettings();
            b.Filters ??= new FilterSettings();
            a.Filters = b.Filters; // replace with user values wholesale

            a.TribeName = string.IsNullOrWhiteSpace(b.TribeName) ? a.TribeName : b.TribeName;
            a.ServerName = string.IsNullOrWhiteSpace(b.ServerName) ? a.ServerName : b.ServerName;

            a.UseCrop |= b.UseCrop;
            a.CropX = b.CropX != 0 ? b.CropX : a.CropX;
            a.CropY = b.CropY != 0 ? b.CropY : a.CropY;
            a.CropW = b.CropW != 0 ? b.CropW : a.CropW;
            a.CropH = b.CropH != 0 ? b.CropH : a.CropH;

            a.IntervalMinutes = b.IntervalMinutes != 0 ? b.IntervalMinutes : a.IntervalMinutes;

            a.OcrPath = string.IsNullOrWhiteSpace(b.OcrPath) ? a.OcrPath : b.OcrPath;
            a.ScreenshotIngestPath = string.IsNullOrWhiteSpace(b.ScreenshotIngestPath) ? a.ScreenshotIngestPath : b.ScreenshotIngestPath;
            a.LogLineIngestPath = string.IsNullOrWhiteSpace(b.LogLineIngestPath) ? a.LogLineIngestPath : b.LogLineIngestPath;

            return a;
        }

        private static void Migrate(AppSettings s)
        {
            // Promote legacy shared key
            if (string.IsNullOrWhiteSpace(s.Auth.ApiKey) && !string.IsNullOrWhiteSpace(s.Auth.SharedKey))
                s.Auth.ApiKey = s.Auth.SharedKey;

            // Normalize base URL
            if (!string.IsNullOrWhiteSpace(s.ApiBaseUrl))
                s.ApiBaseUrl = s.ApiBaseUrl.Trim().TrimEnd('/');

            // Clamp crop
            s.CropX = Math.Max(0, s.CropX);
            s.CropY = Math.Max(0, s.CropY);
            s.CropW = Math.Max(0, s.CropW);
            s.CropH = Math.Max(0, s.CropH);

            // Quality
            s.Image.JpegQuality = Math.Clamp(s.Image.JpegQuality, 10, 100);
        }
    }
}
