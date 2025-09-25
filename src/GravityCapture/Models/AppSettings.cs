// src/GravityCapture/Models/AppSettings.cs
using System;
using System.IO;
using System.Text.Json;

namespace GravityCapture.Models
{
    public partial class AppSettings
    {
        public string? ApiBaseUrl { get; set; }
        public AuthSettings? Auth { get; set; }
        public ImageSettings? Image { get; set; }
        public CaptureSettings? Capture { get; set; }

        public string? TribeName { get; set; }
        public int IntervalMinutes { get; set; } = 1;
        public bool AutoOcrEnabled { get; set; } = false;
        public bool PostOnlyCritical { get; set; } = false;

        // crop
        public bool UseCrop { get; set; } = false;
        public double CropX { get; set; }
        public double CropY { get; set; }
        public double CropW { get; set; }
        public double CropH { get; set; }

        public sealed class AuthSettings
        {
            public string? ApiKey { get; set; }
            public string? SharedKey { get; set; } // backward compat
        }

        public sealed class ImageSettings
        {
            public string? ChannelId { get; set; }
            public int JpegQuality { get; set; } = 90;
            public string? TargetWindowHint { get; set; } = "ARK";
            public bool FilterTameDeath { get; set; }
            public bool FilterStructureDestroyed { get; set; }
            public bool FilterTribeMateDeath { get; set; }
        }

        public sealed class CaptureSettings
        {
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

                // seed from appsettings.Stage.json (if present)
                var exeDir = AppContext.BaseDirectory;
                var seedPath = Path.Combine(exeDir, "appsettings.Stage.json");
                AppSettings seed = new();
                if (File.Exists(seedPath))
                {
                    var seedJson = File.ReadAllText(seedPath);
                    seed = JsonSerializer.Deserialize<AppSettings>(seedJson) ?? new AppSettings();
                }

                // overlay with persisted global.json
                if (File.Exists(GlobalPath))
                {
                    var gj = File.ReadAllText(GlobalPath);
                    var persisted = JsonSerializer.Deserialize<AppSettings>(gj);
                    if (persisted != null) seed = Merge(seed, persisted);
                }

                seed.Auth ??= new AuthSettings();
                seed.Image ??= new ImageSettings();
                seed.Capture ??= new CaptureSettings();

                if (string.IsNullOrWhiteSpace(seed.Auth.ApiKey) && !string.IsNullOrWhiteSpace(seed.Auth.SharedKey))
                    seed.Auth.ApiKey = seed.Auth.SharedKey;

                return seed;
            }
            catch
            {
                return new AppSettings
                {
                    Auth = new AuthSettings(),
                    Image = new ImageSettings(),
                    Capture = new CaptureSettings()
                };
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, JOpts);
            File.WriteAllText(GlobalPath, json);
        }

        // shallow merge: persisted overrides seed
        private static AppSettings Merge(AppSettings a, AppSettings b)
        {
            a.ApiBaseUrl = FirstNonEmpty(b.ApiBaseUrl, a.ApiBaseUrl);
            a.Auth ??= new AuthSettings();
            b.Auth ??= new AuthSettings();
            a.Auth.ApiKey = FirstNonEmpty(b.Auth.ApiKey, a.Auth.ApiKey);
            a.Auth.SharedKey = FirstNonEmpty(b.Auth.SharedKey, a.Auth.SharedKey);

            a.Image ??= new ImageSettings();
            b.Image ??= new ImageSettings();
            a.Image.ChannelId = FirstNonEmpty(b.Image.ChannelId, a.Image.ChannelId);
            a.Image.JpegQuality = b.Image.JpegQuality != 0 ? b.Image.JpegQuality : a.Image.JpegQuality;
            a.Image.TargetWindowHint = FirstNonEmpty(b.Image.TargetWindowHint, a.Image.TargetWindowHint);
            a.Image.FilterTameDeath = b.Image.FilterTameDeath || a.Image.FilterTameDeath;
            a.Image.FilterStructureDestroyed = b.Image.FilterStructureDestroyed || a.Image.FilterStructureDestroyed;
            a.Image.FilterTribeMateDeath = b.Image.FilterTribeMateDeath || a.Image.FilterTribeMateDeath;

            a.Capture ??= new CaptureSettings();
            b.Capture ??= new CaptureSettings();
            a.Capture.ActiveWindow = b.Capture.ActiveWindow || a.Capture.ActiveWindow;
            a.Capture.ServerName = FirstNonEmpty(b.Capture.ServerName, a.Capture.ServerName);

            a.TribeName = FirstNonEmpty(b.TribeName, a.TribeName);
            a.IntervalMinutes = b.IntervalMinutes != 0 ? b.IntervalMinutes : a.IntervalMinutes;
            a.AutoOcrEnabled = b.AutoOcrEnabled || a.AutoOcrEnabled;
            a.PostOnlyCritical = b.PostOnlyCritical || a.PostOnlyCritical;

            a.UseCrop = b.UseCrop || a.UseCrop;
            a.CropX = b.CropX != 0 ? b.CropX : a.CropX;
            a.CropY = b.CropY != 0 ? b.CropY : a.CropY;
            a.CropW = b.CropW != 0 ? b.CropW : a.CropW;
            a.CropH = b.CropH != 0 ? b.CropH : a.CropH;

            return a;
        }

        private static string FirstNonEmpty(string? x, string? y) =>
            !string.IsNullOrWhiteSpace(x) ? x! : (y ?? "");
    }
}
