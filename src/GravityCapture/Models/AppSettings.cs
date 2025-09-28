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

        public string? TribeName { get; set; }
        public int IntervalMinutes { get; set; } = 1;
        public bool AutoOcrEnabled { get; set; } = false;
        public bool PostOnlyCritical { get; set; } = false;

        // Crop selection (normalized pixels in screen coordinates)
        public bool UseCrop { get; set; } = false;
        public double CropX { get; set; }
        public double CropY { get; set; }
        public double CropW { get; set; }
        public double CropH { get; set; }

        public sealed class AuthSettings
        {
            public string? ApiKey { get; set; }
            // legacy name we still accept if ApiKey is empty
            public string? SharedKey { get; set; }
        }

        public sealed class ImageSettings
        {
            public string? ChannelId { get; set; }

            /// <summary>
            /// Always saved as 100 now; kept only for backward compatibility with older JSON.
            /// </summary>
            public int JpegQuality { get; set; } = 100;

            /// <summary>Window title hint used to locate ARK. Example: "ARK" or "ArkAscended".</summary>
            public string? TargetWindowHint { get; set; } = "ARK";

            // Filter toggles
            public bool FilterTameDeath { get; set; }
            public bool FilterStructureDestroyed { get; set; }
            public bool FilterTribeMateDeath { get; set; }
        }

        public sealed class CaptureSettings
        {
            /// <summary>
            /// Backward-compat only. UI no longer exposes this. We still read/write to avoid
            /// breaking existing global.json files.
            /// </summary>
            public bool ActiveWindow { get; set; } = true;

            public string? ServerName { get; set; }
        }

        // -------- persistence --------
        private static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GravityCapture");
        private static string GlobalPath => Path.Combine(ConfigDir, "global.json");
        private static readonly JsonSerializerOptions JOpts = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);

                // 1) Optional seed (repo-side defaults)
                var exeDir = AppContext.BaseDirectory;
                var seedPath = Path.Combine(exeDir, "appsettings.Stage.json");
                var result = File.Exists(seedPath)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(seedPath)) ?? new AppSettings()
                    : new AppSettings();

                // 2) Overlay with persisted global.json (user overrides)
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
                // Fail-safe: return a valid object so the app keeps running
                var safe = new AppSettings();
                MigrateAndSanitize(safe);
                return safe;
            }
        }

        public void Save()
        {
            // enforce new invariants on write
            MigrateAndSanitize(this);
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(GlobalPath, JsonSerializer.Serialize(this, JOpts));
        }

        // shallow merge: b overrides a
        private static AppSettings Merge(AppSettings a, AppSettings b)
        {
            a.ApiBaseUrl = First(b.ApiBaseUrl, a.ApiBaseUrl);

            a.Auth ??= new AuthSettings();
            b.Auth ??= new AuthSettings();
            a.Auth.ApiKey    = First(b.Auth.ApiKey, a.Auth.ApiKey);
            a.Auth.SharedKey = First(b.Auth.SharedKey, a.Auth.SharedKey);

            a.Image ??= new ImageSettings();
            b.Image ??= new ImageSettings();
            a.Image.ChannelId = First(b.Image.ChannelId, a.Image.ChannelId);
            a.Image.JpegQuality = b.Image.JpegQuality != 0 ? b.Image.JpegQuality : a.Image.JpegQuality;
            a.Image.TargetWindowHint = First(b.Image.TargetWindowHint, a.Image.TargetWindowHint);
            a.Image.FilterTameDeath |= b.Image.FilterTameDeath;
            a.Image.FilterStructureDestroyed |= b.Image.FilterStructureDestroyed;
            a.Image.FilterTribeMateDeath |= b.Image.FilterTribeMateDeath;

            a.Capture ??= new CaptureSettings();
            b.Capture ??= new CaptureSettings();
            a.Capture.ActiveWindow |= b.Capture.ActiveWindow;
            a.Capture.ServerName = First(b.Capture.ServerName, a.Capture.ServerName);

            a.TribeName = First(b.TribeName, a.TribeName);
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
        }

        private static string First(string? x, string? y) =>
            !string.IsNullOrWhiteSpace(x) ? x! : (y ?? "");
    }
}
