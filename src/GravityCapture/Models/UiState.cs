using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GravityCapture;

/// <summary>
/// Lightweight UI state persistence so the form re-opens with the last values
/// even if AppSettings hasn't saved them yet. Lives alongside global.json and
/// only stores UI-only fields (Server, Tribe, API URL/Key).
/// </summary>
public sealed class UiState
{
    public string ApiUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Server { get; set; } = "";
    public string Tribe  { get; set; } = "";

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GravityCapture");
    private static string PathFile => System.IO.Path.Combine(Dir, "ui.json");

    public static UiState Load()
    {
        try
        {
            if (File.Exists(PathFile))
            {
                var json = File.ReadAllText(PathFile, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<UiState>(json);
                return loaded ?? new UiState();
            }
        }
        catch { /* ignore and fall back */ }
        return new UiState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathFile, json, Encoding.UTF8);
        }
        catch { /* ignore */ }
    }
}
