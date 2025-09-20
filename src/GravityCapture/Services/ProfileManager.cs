using System;
using System.Collections.Generic;
// using System.CommandLine; // removed: not used
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
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, "profiles");
            Directory.CreateDirectory(baseDir);
            _configPath = Path.Combine(baseDir, ProfilesFileName);

            if (!File.Exists(_configPath))
            {
                TrySeedFromRepoDefaults(baseDir);
            }

            Load();

            string? cliProfile = null;
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
                    {
                        cliProfile = a.Substring("--profile=".Length).Trim();
                    }
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
