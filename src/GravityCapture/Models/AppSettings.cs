using System;
using System.IO;
using System.Text.Json;

namespace GravityCapture.Models
{
    public class AppSettings
    {
        public string ApiUrl { get; set; } = "http://localhost:8080/api/screenshots";
        public string ApiKey { get; set; } = "CHANGE_ME";
        public ulong ChannelId { get; set; } = 0;
        public int IntervalMinutes { get; set; } = 5;
        public bool CaptureActiveWindow { get; set; } = false;
        public int JpegQuality { get; set; } = 85;
        public bool Autostart { get; set; } = false;

        public static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GravityCapture", "settings.json");

        public void Save()
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static AppSettings Load()
        {
            if (File.Exists(SettingsPath))
            {
                try {
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                } catch { }
            }
            return new AppSettings();
        }
    }
}
