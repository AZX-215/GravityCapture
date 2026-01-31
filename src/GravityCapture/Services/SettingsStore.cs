using System;
using System.IO;
using System.Text;
using System.Text.Json;
using GravityCapture.Models;

namespace GravityCapture.Services;

public sealed class SettingsStore
{
    // Primary path is LocalAppData (more reliable than Roaming/AppData for many setups).
    // We keep a fallback path to Roaming/AppData to migrate older installs.
    private readonly string _primaryDir;
    private readonly string _primaryPath;

    private readonly string _fallbackDir;
    private readonly string _fallbackPath;

    public SettingsStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // If LocalAppData is unavailable for some reason, fall back to Roaming.
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = roamingAppData;

        _primaryDir = Path.Combine(localAppData ?? string.Empty, "GravityCapture");
        _primaryPath = Path.Combine(_primaryDir, "settings.json");

        _fallbackDir = Path.Combine(roamingAppData ?? string.Empty, "GravityCapture");
        _fallbackPath = Path.Combine(_fallbackDir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            // Prefer the primary path, but if it doesn't exist and the fallback does,
            // load from fallback and migrate to primary.
            var pathToUse = File.Exists(_primaryPath)
                ? _primaryPath
                : (File.Exists(_fallbackPath) ? _fallbackPath : null);

            if (string.IsNullOrWhiteSpace(pathToUse))
                return new AppSettings();

            var json = File.ReadAllText(pathToUse);
            var s = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AppSettings();

            s.CaptureRegion?.Clamp();

            // Migrate any old roaming/AppData settings to the primary location.
            if (!string.Equals(pathToUse, _primaryPath, StringComparison.OrdinalIgnoreCase))
            {
                try { Save(s); } catch { /* ignore migration failures */ }
            }
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.CaptureRegion?.Clamp();

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        // Ensure we can write reliably even if the destination file is locked momentarily.
        Directory.CreateDirectory(_primaryDir);

        var tmpPath = _primaryPath + ".tmp";
        File.WriteAllText(tmpPath, json, Encoding.UTF8);

        try
        {
            // Best-effort atomic replace.
            if (File.Exists(_primaryPath))
                File.Replace(tmpPath, _primaryPath, null);
            else
                File.Move(tmpPath, _primaryPath);
        }
        catch
        {
            // Fallback: overwrite by copy/move.
            try
            {
                if (File.Exists(_primaryPath))
                    File.Delete(_primaryPath);
                File.Move(tmpPath, _primaryPath);
            }
            finally
            {
                // Clean up temp if something went wrong.
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* ignore */ }
            }
        }
    }

    public string SettingsPath => _primaryPath;
}
