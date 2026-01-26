using System;
using System.IO;
using System.Text.Json;
using GravityCapture.Models;

namespace GravityCapture.Services;

public sealed class SettingsStore
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsStore()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GravityCapture");
        _path = Path.Combine(_dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();

            var json = File.ReadAllText(_path);
            var s = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AppSettings();

            s.CaptureRegion?.Clamp();
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_dir);
        settings.CaptureRegion?.Clamp();

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_path, json);
    }

    public string SettingsPath => _path;
}
