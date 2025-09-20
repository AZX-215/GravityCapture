// ================================================
// File: src/GravityCapture/Models/OcrProfile.cs
// ================================================
using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    public sealed class OcrProfile
    {
        [JsonPropertyName("GC_OCR_TONEMAP")] public int TONEMAP { get; set; } = 0;
        [JsonPropertyName("GC_OCR_ADAPTIVE")] public int ADAPTIVE { get; set; } = 0;
        [JsonPropertyName("GC_OCR_ADAPTIVE_WIN")] public int ADAPTIVE_WIN { get; set; } = 19;
        [JsonPropertyName("GC_OCR_ADAPTIVE_C")] public int ADAPTIVE_C { get; set; } = 0;
        [JsonPropertyName("GC_OCR_SHARPEN")] public int SHARPEN { get; set; } = 0;
        [JsonPropertyName("GC_OCR_OPEN")] public int OPEN { get; set; } = 0;
        [JsonPropertyName("GC_OCR_CLOSE")] public int CLOSE { get; set; } = 0;
        [JsonPropertyName("GC_OCR_DILATE")] public int DILATE { get; set; } = 0;
        [JsonPropertyName("GC_OCR_ERODE")] public int ERODE { get; set; } = 0;
        [JsonPropertyName("GC_OCR_CONTRAST")] public double CONTRAST { get; set; } = 1.0;
        [JsonPropertyName("GC_OCR_INVERT")] public int INVERT { get; set; } = 0;
        [JsonPropertyName("GC_OCR_MAJORITY")] public int MAJORITY { get; set; } = 0;
        [JsonPropertyName("GC_OCR_MAJORITY_ITERS")] public int MAJORITY_ITERS { get; set; } = 0;
        [JsonPropertyName("GC_OCR_OPEN_ITERS")] public int OPEN_ITERS { get; set; } = 0;
        [JsonPropertyName("GC_OCR_UPSCALE")] public int UPSCALE { get; set; } = 1;
    }
}

// ================================================
// File: src/GravityCapture/Models/ProfilesContainer.cs
// ================================================
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GravityCapture.Models
{
    public sealed class ProfilesContainer
    {
        [JsonPropertyName("activeProfile")] public string ActiveProfile { get; set; } = "HDR";
        [JsonPropertyName("profiles")] public Dictionary<string, OcrProfile> Profiles { get; set; } = new();
    }
}

// ================================================
// File: src/GravityCapture/Services/ProfileManager.cs
// ================================================
using System;
using System.Collections.Generic;
using System.CommandLine; // If not present, remove CLI support or add System.CommandLine via NuGet
using System.IO;
using System.Text;
using System.Text.Json;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    public static class ProfileManager
    {
        public const string AppFolderName = "GravityCapture";
        public const string ProfilesFileName = "profiles.json";

        public static event Action<string, OcrProfile>? ProfileChanged;

        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static ProfilesContainer _container = new();
        private static string _active = "HDR";
        private static string _configPath = string.Empty;

        public static string ActiveProfile => _active;
        public static OcrProfile Current => Get(_active);

        public static void Initialize(string[]? args = null)
        {
            // 1) Resolve config location: %APPDATA%/GravityCapture/profiles/profiles.json
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, "profiles");
            Directory.CreateDirectory(baseDir);
            _configPath = Path.Combine(baseDir, ProfilesFileName);

            // 2) If missing, seed from embedded defaults in repo path (optional) or built-in defaults
            if (!File.Exists(_configPath))
            {
                TrySeedFromRepoDefaults(baseDir);
            }

            // 3) Load container
            Load();

            // 4) Apply override sources in priority order: CLI > ENV > file
            string? cliProfile = null;
            if (args != null)
            {
                // Minimal manual parse to avoid dependency if System.CommandLine not desired
                foreach (var a in args)
                {
                    if (a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
                    {
                        cliProfile = a.Substring("--profile=".Length).Trim();
                    }
                }
            }

            var envProfile = Environment.GetEnvironmentVariable("GC_PROFILE")
                             ?? Environment.GetEnvironmentVariable("GC_ENV"); // back-compat: HDR/SDR

            var target = cliProfile ?? envProfile ?? _container.ActiveProfile;
            Switch(target);
        }

        public static void Switch(string? profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) profileName = _container.ActiveProfile;
            if (!_container.Profiles.TryGetValue(profileName!, out var prof))
            {
                // Fallback to HDR if requested profile missing
                profileName = "HDR";
                prof = _container.Profiles.ContainsKey("HDR") ? _container.Profiles["HDR"] : BuildDefaultHdr();
            }
            _active = profileName!;
            _container.ActiveProfile = _active;
            Save();
            ProfileChanged?.Invoke(_active, prof);
        }

        public static OcrProfile Get(string profileName)
        {
            if (_container.Profiles.TryGetValue(profileName, out var prof)) return prof;
            // Ensure map contains defaults
            if (!_container.Profiles.ContainsKey("HDR")) _container.Profiles["HDR"] = BuildDefaultHdr();
            if (!_container.Profiles.ContainsKey("SDR")) _container.Profiles["SDR"] = BuildDefaultSdr();
            return _container.Profiles[profileName] = profileName.Equals("SDR", StringComparison.OrdinalIgnoreCase)
                ? BuildDefaultSdr() : BuildDefaultHdr();
        }

        public static void Update(string profileName, OcrProfile updated)
        {
            _container.Profiles[profileName] = updated;
            if (string.Equals(profileName, _active, StringComparison.OrdinalIgnoreCase))
            {
                ProfileChanged?.Invoke(_active, updated);
            }
            Save();
        }

        public static IReadOnlyDictionary<string, OcrProfile> All() => _container.Profiles;

        private static void Load()
        {
            if (!File.Exists(_configPath))
            {
                _container = new ProfilesContainer
                {
                    ActiveProfile = "HDR",
                    Profiles = new Dictionary<string, OcrProfile>
                    {
                        ["HDR"] = BuildDefaultHdr(),
                        ["SDR"] = BuildDefaultSdr()
                    }
                };
                Save();
                return;
            }
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            _container = JsonSerializer.Deserialize<ProfilesContainer>(json, _json) ?? new ProfilesContainer();
            if (_container.Profiles == null || _container.Profiles.Count == 0)
            {
                _container.Profiles = new Dictionary<string, OcrProfile>
                {
                    ["HDR"] = BuildDefaultHdr(),
                    ["SDR"] = BuildDefaultSdr()
                };
            }
        }

        private static void Save()
        {
            var json = JsonSerializer.Serialize(_container, _json);
            File.WriteAllText(_configPath, json, Encoding.UTF8);
        }

        private static void TrySeedFromRepoDefaults(string baseDir)
        {
            try
            {
                // Optional: read repo default at src/GravityCapture/Config/default.profiles.json if alongside exe
                var exeDir = AppContext.BaseDirectory;
                var candidate = Path.Combine(exeDir, "Config", "default.profiles.json");
                if (File.Exists(candidate))
                {
                    Directory.CreateDirectory(baseDir);
                    File.Copy(candidate, Path.Combine(baseDir, ProfilesFileName), overwrite: true);
                    return;
                }
            }
            catch { /* ignore */ }

            // Fallback: write built-in defaults
            _container = new ProfilesContainer
            {
                ActiveProfile = "HDR",
                Profiles = new Dictionary<string, OcrProfile>
                {
                    ["HDR"] = BuildDefaultHdr(),
                    ["SDR"] = BuildDefaultSdr()
                }
            };
            Save();
        }

        private static OcrProfile BuildDefaultHdr() => new()
        {
            TONEMAP = 0,
            ADAPTIVE = 1,
            ADAPTIVE_WIN = 19,
            ADAPTIVE_C = 0,
            SHARPEN = 1,
            OPEN = 0,
            CLOSE = 0,
            DILATE = 0,
            ERODE = 5,
            CONTRAST = 1.6,
            INVERT = 1,
            MAJORITY = 0,
            MAJORITY_ITERS = 3,
            OPEN_ITERS = 2,
            UPSCALE = 2
        };

        private static OcrProfile BuildDefaultSdr() => new()
        {
            TONEMAP = 0,
            ADAPTIVE = 1,
            ADAPTIVE_WIN = 16,
            ADAPTIVE_C = 2,
            SHARPEN = 1,
            OPEN = 1,
            CLOSE = 0,
            DILATE = 0,
            ERODE = 2,
            CONTRAST = 1.2,
            INVERT = 1,
            MAJORITY = 0,
            MAJORITY_ITERS = 2,
            OPEN_ITERS = 2,
            UPSCALE = 2
        };
    }
}

// ================================================
// File: src/GravityCapture/Config/default.profiles.json
// ================================================
{
  "activeProfile": "HDR",
  "profiles": {
    "HDR": {
      "GC_OCR_TONEMAP": 0,
      "GC_OCR_ADAPTIVE": 1,
      "GC_OCR_ADAPTIVE_WIN": 19,
      "GC_OCR_ADAPTIVE_C": 0,
      "GC_OCR_SHARPEN": 1,
      "GC_OCR_OPEN": 0,
      "GC_OCR_CLOSE": 0,
      "GC_OCR_DILATE": 0,
      "GC_OCR_ERODE": 5,
      "GC_OCR_CONTRAST": 1.6,
      "GC_OCR_INVERT": 1,
      "GC_OCR_MAJORITY": 0,
      "GC_OCR_MAJORITY_ITERS": 3,
      "GC_OCR_OPEN_ITERS": 2,
      "GC_OCR_UPSCALE": 2
    },
    "SDR": {
      "GC_OCR_TONEMAP": 0,
      "GC_OCR_ADAPTIVE": 1,
      "GC_OCR_ADAPTIVE_WIN": 16,
      "GC_OCR_ADAPTIVE_C": 2,
      "GC_OCR_SHARPEN": 1,
      "GC_OCR_OPEN": 1,
      "GC_OCR_CLOSE": 0,
      "GC_OCR_DILATE": 0,
      "GC_OCR_ERODE": 2,
      "GC_OCR_CONTRAST": 1.2,
      "GC_OCR_INVERT": 1,
      "GC_OCR_MAJORITY": 0,
      "GC_OCR_MAJORITY_ITERS": 2,
      "GC_OCR_OPEN_ITERS": 2,
      "GC_OCR_UPSCALE": 2
    }
  }
}

// ================================================
// Integration points (minimal)
// ================================================
// 1) Call Initialize as early as possible (e.g., App.xaml.cs OnStartup or first line of Main):
//    ProfileManager.Initialize(Environment.GetCommandLineArgs());
//    You can pass "--profile=hdr" or set ENV GC_PROFILE=HDR or GC_ENV=HDR.

// 2) Wherever OCR pipeline previously read debug env vars, replace with ProfileManager.Current values:
//    var p = ProfileManager.Current;
//    var tonemap = p.TONEMAP;
//    var adaptive = p.ADAPTIVE;
//    var win = p.ADAPTIVE_WIN;
//    var c = p.ADAPTIVE_C;
//    var sharpen = p.SHARPEN;
//    var open = p.OPEN;
//    var close = p.CLOSE;
//    var dilate = p.DILATE;
//    var erode = p.ERODE;
//    var contrast = p.CONTRAST;
//    var invert = p.INVERT;
//    var majority = p.MAJORITY;
//    var majIters = p.MAJORITY_ITERS;
//    var openIters = p.OPEN_ITERS;
//    var upscale = p.UPSCALE;

// 3) Optional runtime switch (tray/menu button):
//    ProfileManager.Switch("SDR"); // fires ProfileChanged event so your pipeline can reload params.

// 4) Optional: subscribe to changes
//    ProfileManager.ProfileChanged += (name, profile) => { /* rebind OCR parameters */ };

// 5) Distribution:
//    On first run, defaults are copied to %APPDATA%/GravityCapture/profiles/profiles.json.
//    Users can edit this file to tweak SDR without touching HDR.
