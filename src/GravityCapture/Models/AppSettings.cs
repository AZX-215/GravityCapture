using System;
using System.IO;
using System.Text.Json;

namespace GravityCapture.Models
{
    public sealed class AppSettings
    {
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public ulong ChannelId { get; set; }
        public int IntervalMinutes { get; set; } = 5;
        public bool CaptureActiveWindow { get; set; } = true;
        public int JpegQuality { get; set; } = 85;
        public bool Autostart { get; set; } = false;

        // --- New: crop settings (normalized to window client rect) ---
        // Target window hint (substring match on window title; leave blank = use foreground)
        public string TargetWindowHint { get; set; } = "ARK: Survival Ascended";
        public bool UseCrop { get; set; } = false;
        public double CropX { get; set; } = 0;   // 0..1
        public double CropY { get; set; } = 0;
        public double CropW { get; set; } = 1;
        public double CropH { get; set; } = 1;

        public static string Path =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "GravityCapture", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(Path))
                {
                    var json = File.ReadAllText(Path);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path, json);
            }
            catch { }
        }
    }
}
