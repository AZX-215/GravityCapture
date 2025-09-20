using System;
using System.Collections.Generic;
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
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolderName, "profiles");
            Directory.CreateDirectory(baseDir);
            _configPath = Path.Combine(baseDir, ProfilesFileName);

            // Allow explicit reseed via env var (opt-in).
            bool forceReset = string.Equals(
                Environment.GetEnvironmentVariable("GC_FORCE_PROFILE_RESET"), "1",
                StringComparison.OrdinalIgnoreCase);

            if (!File.Exists(_configPath) || forceReset)
            {
                TrySeedFromRepoDefaults(baseDir, overwrite: true);
            }

            Load();

            string? cliProfile = null;
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
                        cliProfile = a.Substring("--profile=".Length).Trim();
                }
            }

            var envProfile = Environment.GetEnvironmentVariable("GC_PROFILE")
                             ?? Environment.GetEnvironmentVariable("GC_ENV");

            var target = cliProfile ?? envProfile ?? _container.ActiveProfile;
            Switch(target);
        }

        public static void Switch(string? profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) profileName = _container.ActiveProfile;
            if (!_container.Profiles.TryGetValue(profileName!, out var prof))
            {
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

            if (!_container.Profiles.ContainsKey("HDR")) _container.Profiles["HDR"] = BuildDefaultHdr();
            if (!_container.Profiles.ContainsKey("SDR")) _container.Profiles["SDR"] = BuildDefaultSdr();

            return _container.Profiles[profileName] =
                profileName.Equals("SDR", StringComparison.OrdinalIgnoreCase)
                ? BuildDefaultSdr()
                : BuildDefaultHdr();
        }

        public static void Update(string profileName, OcrProfile updated)
        {
            _container.Profiles[profileName] = updated;
            if (string.Equals(profileName, _active, StringComparison.OrdinalIgnoreCase))
                ProfileChanged?.Invoke(_active, updated);
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
            if (string.IsNullOrWhiteSpace(_container.ActiveProfile))
                _container.ActiveProfile = "HDR";
        }

        private static void Save()
        {
            var json = JsonSerializer.Serialize(_container, _json);
            File.WriteAllText(_configPath, json, Encoding.UTF8);
        }

        /// <summary>
        /// Copy Config\default.profiles.json from the app's output folder into Roaming profiles,
        /// but only when missing by default. If overwrite==true, replace it intentionally.
        /// If the file is not present in output, fall back to hardcoded defaults without clobbering existing files.
        /// </summary>
        private static void TrySeedFromRepoDefaults(string baseDir, bool overwrite = false)
        {
            try
            {
                var exeDir = AppContext.BaseDirectory;
                var candidate = Path.Combine(exeDir, "Config", "default.profiles.json");
                var dest = Path.Combine(baseDir, ProfilesFileName);

                if (File.Exists(candidate))
                {
                    if (!File.Exists(dest) || overwrite)
                    {
                        Directory.CreateDirectory(baseDir);
                        File.Copy(candidate, dest, overwrite: true);
                        return;
                    }
                    // File exists and overwrite not requested → do nothing.
                    return;
                }
            }
            catch
            {
                // ignore and fall through to hardcoded defaults if needed
            }

            // Only generate from fallback if the file truly doesn't exist or overwrite requested.
            var destPath = Path.Combine(baseDir, ProfilesFileName);
            if (!File.Exists(destPath) || overwrite)
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
            }
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

        // SDR fallback updated exactly to your requested test values.
        // Note: fields not present on OcrProfile are intentionally omitted here to avoid build breaks.
        private static OcrProfile BuildDefaultSdr() => new()
{
    TONEMAP = 1,
    ADAPTIVE = 1,
    ADAPTIVE_WIN = 31,
    ADAPTIVE_C = -28,

    SHARPEN = 0,

    OPEN = 0,
    OPEN_ITERS = 0,

    CLOSE = 0,

    DILATE = 2,
    ERODE = 0,

    CONTRAST = 1.30,

    INVERT = 1,

    MAJORITY = 0,
    MAJORITY_ITERS = 0,

    UPSCALE = 3
};

    }
}
