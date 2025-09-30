// src/GravityCapture/Models/AppSettings.cs
using System;
using System.IO;
using System.Text.Json;

namespace GravityCapture.Models
{
    /// <summary>
    /// Single persisted settings object (global.json in %LOCALAPPDATA%\GravityCapture).
    /// Seeds from appsettings.Stage.json (if present) then overlays with persisted values.
    /// </summary>
    public partial class AppSettings
    {
        public string? ApiBaseUrl { get; set; }
        public AuthSettings Auth { get; set; } = new();
        public ImageSettings Image { get; set; } = new();
        public CaptureSettings Capture { get; set; } = new();

        public string? ServerName { get; set; }           // legacy
        public string? TribeName { get; set; }
        public bool UseCrop { get; set; }
        public int CropX { get; set; }
        public int CropY { get; set; }
        public int CropW { get; set; }
        public int CropH { get; set; }

        public int IntervalMinutes { get; set; } = 0;     // 0 => default 2.5s

        public bool AutoOcrEnabled { get; set; }          // ignored in UI
        public bool PostOnlyCritical { get; set; }

        public sealed class AuthSettings
        {
            public string? ApiKey { get; set; }
            public string? SharedKey { get; set; } // legacy alias
        }

        public sealed class ImageSettings
        {
            public string? ChannelId { get; set; }

            /// <summary>Always saved as 100 now; kept for back-compat.</summary>
            public int JpegQuality { get; set; } = 100;

            /// <summary>Window title hint used to locate ARK.</summary>
            public string? TargetWindowHint { get; set; } = "ARK";

            // Legacy UI filters (we will sanitize to false on save)
            public bool FilterTameDeath { get; set; }
            public bool FilterStructureDestroyed { get; set; }
            public bool FilterTribeMateDeath { get; set; }
        }

        public sealed class CaptureSettings
        {
            public bool ActiveWindow { get; set; } = true; // back-compat
            public string? ServerName { get; set; }
            public string? TargetWindowHint { get; set; }  // optional per-capture override
        }

        // -------- persistence --------
        private static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GravityCapture");
        private static string GlobalPath => Path.Combine(ConfigDir, "global.json");
        private static string StageSeedPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? "", "appsettings.Stage.json");

        private static readonly JsonSerializerOptions JOpts = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                // 1) Seed from appsettings.Stage.json
                var result = new AppSettings();
                if (File.Exists(StageSeedPath))
                {
                    var seeded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StageSeedPath));
                    if (seeded != null) result = Merge(result, seeded);
                }

                // 2) Overlay persisted global.json
                if (File.Exists(GlobalPath))
                {
                    var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(GlobalPath));
                    if (persisted != null) result = Merge(result, persisted);
                }

                // 3) Migrate legacy fields / sanitize
                MigrateAndSanitize(result);
                return result;
            }
            catch
            {
                var safe = new AppSettings();
                MigrateAndSanitize(safe);
                return safe;
            }
        }

        public void Save()
        {
            MigrateAndSanitize(this);
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(GlobalPath, JsonSerializer.Serialize(this, JOpts));
        }

        // shallow merge: b overrides a
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

            a.TribeName = string.IsNullOrWhiteSpace(b.TribeName) ? a.TribeName : b.TribeName;
            a.ServerName = string.IsNullOrWhiteSpace(b.ServerName) ? a.ServerName : b.ServerName;
            a.IntervalMinutes = b.IntervalMinutes != 0 ? b.IntervalMinutes : a.IntervalMinutes;
            a.AutoOcrEnabled |= b.AutoOcrEnabled;
            a.PostOnlyCritical |= b.PostOnlyCritical;

            a.UseCrop |= b.UseCrop;
            a.CropX = b.CropX != 0 ? b.CropX : a.CropX;
            a.CropY = b.CropY != 0 ? b.CropY : a.CropY;
            a.CropW = b.CropW != 0 ? b.CropW : a.CropW;
            a.CropH = b.CropH != 0 ? b.CropH : a.CropH;

            return a;
        }

        private static void MigrateAndSanitize(AppSettings s)
        {
            // Legacy: if ApiKey missing but SharedKey present, promote it.
            if (string.IsNullOrWhiteSpace(s.Auth.ApiKey) && !string.IsNullOrWhiteSpace(s.Auth.SharedKey))
                s.Auth.ApiKey = s.Auth.SharedKey;

            // Always keep JPEG quality at 100 moving forward.
            s.Image.JpegQuality = 100;

            // Normalize API base URL (no trailing slash)
            if (!string.IsNullOrWhiteSpace(s.ApiBaseUrl))
                s.ApiBaseUrl = s.ApiBaseUrl!.Trim().TrimEnd('/');

            // Clamp crop values to non-negative
            s.CropX = Math.Max(0, s.CropX);
            s.CropY = Math.Max(0, s.CropY);
            s.CropW = Math.Max(0, s.CropW);
            s.CropH = Math.Max(0, s.CropH);

            // Do not persist session-only filters in ImageSettings
            try
            {
                if (s.Image != null)
                {
                    s.Image.FilterTameDeath = false;
                    s.Image.FilterStructureDestroyed = false;
                    s.Image.FilterTribeMateDeath = false;
                }
            }
            catch { }
        }
    }
}
