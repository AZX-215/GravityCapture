using System;
using System.IO;
using System.Text.Json;

namespace GravityCapture.Models
{
    public class AppSettings
    {
        public string ApiUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public ulong ChannelId { get; set; }
        public int IntervalMinutes { get; set; } = 1;
        public bool CaptureActiveWindow { get; set; } = false;
        public int JpegQuality { get; set; } = 85;
        public string TargetWindowHint { get; set; } = "";
        public bool Autostart { get; set; } = false;
        public bool FilterTameDeath { get; set; } = true;
        public bool FilterStructureDestroyed { get; set; } = true;
        public bool FilterTribeMateDeath { get; set; } = true;


        // Crop
        public bool UseCrop { get; set; } = false;
        public double CropX { get; set; }
        public double CropY { get; set; }
        public double CropW { get; set; }
        public double CropH { get; set; }

        // Auto OCR
        public bool AutoOcrEnabled { get; set; } = false;
        public bool PostOnlyCritical { get; set; } = true;

        private static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "GravityCapture", "appsettings.json");

        public static AppSettings Load()
        {
            try
            {
                var path = SettingsPath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }
    }
}
