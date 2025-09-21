using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GravityCapture.Models;

namespace GravityCapture.Services
{
    public static class ProfileManager
    {
        public const string AppFolderName = "GravityCapture";
        public const string ProfilesFileName = "profiles.json";
        private const string StampFileName = "profiles.stamp"; // stores SHA256 of built default

        public static event Action<string, OcrProfile>? ProfileChanged;

        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
        };

        private static ProfilesContainer _container = new();
        private static string _active = "HDR";
        private static string _userProfilesPath = string.Empty;
        private static string _stampPath = string.Empty;

        public static string ActiveProfile => _active;
        public static OcrProfile Current => Get(_active);

        // --- Init / Sync ---

        public static void Initialize(string[]? args = null)
        {
            (_userProfilesPath, _stampPath) = GetUserPaths();

            var builtDefault = GetBuiltDefaultPath();
            var builtHash = ComputeFileHash(builtDefault);
            var currentStamp = ReadStamp(_stampPath);

            bool forceReset = string.Equals(
                Environment.GetEnvironmentVariable("GC_FORCE_PROFILE_RESET"), "1",
                StringComparison.OrdinalIgnoreCase);

            // Preserve previously active name if we must overwrite the user file
            string? preservedActive = null;
            bool hasUserFile = File.Exists(_userProfilesPath);
            if (!hasUserFile || forceReset || !HashesEqual(builtHash, currentStamp))
            {
                if (hasUserFile)
                {
                    try
                    {
                        var prev = JsonSerializer.Deserialize<ProfilesContainer>(
                            File.ReadAllText(_userProfilesPath, Encoding.UTF8), _json);
                        if (!string.IsNullOrWhiteSpace(prev?.ActiveProfile))
                            preservedActive = prev!.ActiveProfile;
                    }
                    catch { /* ignore bad file */ }
                }

                CopyBuiltDefaultToUser(builtDefault, _userProfilesPath);
                WriteStamp(_stampPath, builtHash);
            }

            Load();

            // Restore preserved active when possible
            if (!string.IsNullOrWhiteSpace(preservedActive) &&
                _container.Profiles != null &&
                _container.Profiles.ContainsKey(preservedActive!))
            {
                _container.ActiveProfile = preservedActive!;
                Save();
            }

            // Runtime should reflect saved state immediately
            _active = _container.ActiveProfile;

            // CLI / ENV profile selection
            string? cliProfile = null;
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
                        cliProfile = a["--profile=".Length..].Trim();
                }
            }

            // Only accept GC_PROFILE, and only if it names an existing profile
            string? envProfile = Environment.GetEnvironmentVariable("GC_PROFILE");
            if (string.IsNullOrWhiteSpace(envProfile) || !_container.Profiles.ContainsKey(envProfile))
                envProfile = null;

            // Drop invalid CLI names too
            if (cliProfile != null && !_container.Profiles.ContainsKey(cliProfile))
                cliProfile = null;

            var target = cliProfile ?? envProfile ?? _container.ActiveProfile;

            // Perform switch if runtime differs
            if (!string.Equals(target, _active, StringComparison.OrdinalIgnoreCase))
                Switch(target);

            // Ensure env matches runtime at startup
            ApplyToEnv(Current, _active);
        }

        // --- Public API ---

        public static void Switch(string? profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) profileName = _container.ActiveProfile;

            if (!_container.Profiles.TryGetValue(profileName!, out var prof))
            {
                // fallback to HDR (create it if missing)
                if (!_container.Profiles.TryGetValue("HDR", out prof))
                {
                    prof = BuildDefaultHdr();
                    _container.Profiles["HDR"] = prof;
                }
                profileName = "HDR";
            }

            // only persist if different
            if (!string.Equals(_active, profileName!, StringComparison.OrdinalIgnoreCase))
            {
                _active = profileName!;
                _container.ActiveProfile = _active;
                Save();
                ApplyToEnv(prof, _active);
                ProfileChanged?.Invoke(_active, prof);
            }
            else
            {
                // Re-apply env even if not switching
                ApplyToEnv(prof, profileName!);
            }
        }

        public static OcrProfile Get(string profileName)
        {
            if (_container.Profiles.TryGetValue(profileName, out var prof))
                return prof;

            // ensure both defaults exist once
            if (!_container.Profiles.TryGetValue("HDR", out _))
                _container.Profiles["HDR"] = BuildDefaultHdr();
            if (!_container.Profiles.TryGetValue("SDR", out _))
                _container.Profiles["SDR"] = BuildDefaultSdr();

            // build & cache the requested profile on miss
            prof = profileName.Equals("SDR", StringComparison.OrdinalIgnoreCase)
                ? BuildDefaultSdr()
                : BuildDefaultHdr();

            _container.Profiles[profileName] = prof;
            return prof;
        }

        public static void Update(string profileName, OcrProfile updated)
        {
            _container.Profiles[profileName] = updated;
            if (string.Equals(profileName, _active, StringComparison.OrdinalIgnoreCase))
                ProfileChanged?.Invoke(_active, updated);
            Save();
        }

        public static IReadOnlyDictionary<string, OcrProfile> All() => _container.Profiles;

        // --- IO ---

        private static void Load()
        {
            if (!File.Exists(_userProfilesPath))
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

            var json = File.ReadAllText(_userProfilesPath, Encoding.UTF8);
            _container = JsonSerializer.Deserialize<ProfilesContainer>(json, _json) ?? new ProfilesContainer();

            _container.Profiles ??= new Dictionary<string, OcrProfile>();
            if (_container.Profiles.Count == 0)
            {
                _container.Profiles["HDR"] = BuildDefaultHdr();
                _container.Profiles["SDR"] = BuildDefaultSdr();
            }

            if (string.IsNullOrWhiteSpace(_container.ActiveProfile))
                _container.ActiveProfile = "HDR";
        }

        private static void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_userProfilesPath)!);
            var json = JsonSerializer.Serialize(_container, _json);
            File.WriteAllText(_userProfilesPath, json, Encoding.UTF8);
        }

        // --- Apply current profile to environment (process scope only) ---
        private static void ApplyToEnv(OcrProfile p, string profileName)
        {
            var inv = CultureInfo.InvariantCulture;
            void Set(string k, string v) => Environment.SetEnvironmentVariable(k, v);
            void SetI(string k, int v)   => Set(k, v.ToString(inv));
            void SetD(string k, double v)=> Set(k, v.ToString(inv));
            void SetB(string k, int v)   => Set(k, v != 0 ? "1" : "0");
            void Unset(string k)         => Environment.SetEnvironmentVariable(k, null);

            // Core pipeline knobs
            SetB("GC_OCR_TONEMAP", p.TONEMAP);
            SetB("GC_OCR_ADAPTIVE", p.ADAPTIVE);
            SetI("GC_OCR_ADAPTIVE_WIN", p.ADAPTIVE_WIN);
            SetI("GC_OCR_ADAPTIVE_C", p.ADAPTIVE_C);

            // Dual threshold and explicit thresholds (Profile uses 0..255; service normalizes)
            try { SetB("GC_OCR_DUAL_THR", (int)p.GetType().GetProperty("DUAL_THR")!.GetValue(p)!); } catch { }
            try { SetI("GC_OCR_THR_LOW",  (int)p.GetType().GetProperty("THR_LOW")!.GetValue(p)!); } catch { }
            try { SetI("GC_OCR_THR_HIGH", (int)p.GetType().GetProperty("THR_HIGH")!.GetValue(p)!); } catch { }

            // Morphology + geometric
            SetB("GC_OCR_OPEN", p.OPEN);           SetI("GC_OCR_OPEN_ITERS", p.OPEN_ITERS);
            SetB("GC_OCR_CLOSE", p.CLOSE);         SetI("GC_OCR_CLOSE_ITERS", p.CLOSE_ITERS);
            SetI("GC_OCR_DILATE", p.DILATE);       SetI("GC_OCR_ERODE", p.ERODE);

            // Photometric
            SetD("GC_OCR_CONTRAST", p.CONTRAST);   SetD("GC_OCR_BRIGHT", p.BRIGHT);
            SetB("GC_OCR_INVERT", p.INVERT);
            try { SetB("GC_OCR_PREBLUR", (int)p.GetType().GetProperty("PREBLUR")!.GetValue(p)!); } catch { }
            try { SetI("GC_OCR_PREBLUR_K",(int)p.GetType().GetProperty("PREBLUR_K")!.GetValue(p)!); } catch { }
            try { SetB("GC_OCR_CLAHE",   (int)p.GetType().GetProperty("CLAHE")!.GetValue(p)!); } catch { }
            try { SetD("GC_OCR_GAMMA",   (double)p.GetType().GetProperty("GAMMA")!.GetValue(p)!); } catch { }

            // Majority
            SetB("GC_OCR_MAJORITY", p.MAJORITY);   SetI("GC_OCR_MAJORITY_ITERS", p.MAJORITY_ITERS);

            // Upscale
            SetI("GC_OCR_UPSCALE", p.UPSCALE);

            // HSV gates
            try { SetD("GC_OCR_SAT_COLOR", p.SAT_COLOR); } catch { }
            try { SetD("GC_OCR_VAL_COLOR", p.VAL_COLOR); } catch { }
            try { SetD("GC_OCR_SAT_GRAY",  p.SAT_GRAY);  } catch { }
            try { SetD("GC_OCR_VAL_GRAY",  p.VAL_GRAY);  } catch { }

            // Gray space
            var gsProp = p.GetType().GetProperty("GRAYSPACE");
            if (gsProp != null && gsProp.PropertyType == typeof(string))
            {
                try
                {
                    var val = (string)gsProp.GetValue(p)!;
                    if (!string.IsNullOrWhiteSpace(val)) Set("GC_OCR_GRAYSPACE", val);
                }
                catch { }
            }

            // Per-profile special knobs:
            if (string.Equals(profileName, "SDR", StringComparison.OrdinalIgnoreCase))
            {
                // Enable Sauvola only for SDR
                Set("GC_OCR_BINARIZER", "sauvola");
                SetI("GC_OCR_SAUVOLA_WIN", 61);
                SetD("GC_OCR_SAUVOLA_K", 0.34);
                SetI("GC_OCR_SAUVOLA_R", 128);

                // Enable capture tone tweak only for SDR
                Set("GC_CAPTURE_TONEBOOST", "1");
            }
            else
            {
                // Ensure HDR path unaffected
                Unset("GC_OCR_BINARIZER");
                Unset("GC_OCR_SAUVOLA_WIN");
                Unset("GC_OCR_SAUVOLA_K");
                Unset("GC_OCR_SAUVOLA_R");

                Unset("GC_CAPTURE_TONEBOOST");
            }
        }

        // --- Defaults / Seeding ---

        private static string GetBuiltDefaultPath()
        {
            var exeDir = AppContext.BaseDirectory;
            return Path.Combine(exeDir, "Config", "default.profiles.json");
        }

        private static void CopyBuiltDefaultToUser(string builtDefaultPath, string userPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
            if (File.Exists(builtDefaultPath))
            {
                File.Copy(builtDefaultPath, userPath, overwrite: true);
            }
            else
            {
                // fallback to hardcoded defaults
                var fallback = new ProfilesContainer
                {
                    ActiveProfile = "HDR",
                    Profiles = new Dictionary<string, OcrProfile>
                    {
                        ["HDR"] = BuildDefaultHdr(),
                        ["SDR"] = BuildDefaultSdr()
                    }
                };
                var json = JsonSerializer.Serialize(fallback, _json);
                File.WriteAllText(userPath, json, Encoding.UTF8);
            }
        }

        private static (string userProfiles, string stampPath) GetUserPaths()
        {
            var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dirNew = Path.Combine(app, AppFolderName, "profiles");
            Directory.CreateDirectory(dirNew);
            var newPath = Path.Combine(dirNew, ProfilesFileName);

            // migrate old location if present
            var oldPath = Path.Combine(app, AppFolderName, ProfilesFileName);
            if (File.Exists(oldPath) && !File.Exists(newPath))
            {
                File.Copy(oldPath, newPath, overwrite: true);
                try { File.Delete(oldPath); } catch { }
            }

            var stamp = Path.Combine(dirNew, StampFileName);
            return (newPath, stamp);
        }

        private static byte[]? ComputeFileHash(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(path);
                return sha.ComputeHash(fs);
            }
            catch { return null; }
        }

        private static byte[]? ReadStamp(string stampPath)
        {
            try
            {
                if (!File.Exists(stampPath)) return null;
                var hex = File.ReadAllText(stampPath).Trim();
                return HexToBytes(hex);
            }
            catch { return null; }
        }

        private static void WriteStamp(string stampPath, byte[]? hash)
        {
            try
            {
                if (hash == null) { if (File.Exists(stampPath)) File.Delete(stampPath); return; }
                File.WriteAllText(stampPath, BytesToHex(hash));
            }
            catch { }
        }

        private static bool HashesEqual(byte[]? a, byte[]? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string BytesToHex(byte[] data)
        {
            char[] c = new char[data.Length * 2];
            int b;
            for (int i = 0; i < data.Length; i++)
            {
                b = data[i] >> 4;
                c[i * 2] = (char)(55 + b + (((b - 10) >> 31) & -7));
                b = data[i] & 0xF;
                c[i * 2 + 1] = (char)(55 + b + (((b - 10) >> 31) & -7));
            }
            return new string(c);
        }

        private static byte[]? HexToBytes(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            int len = hex.Length;
            if (len % 2 != 0) return null;
            var data = new byte[len / 2];
            for (int i = 0; i < len; i += 2)
                data[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return data;
        }

        // --- Hardcoded defaults ---

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
            ADAPTIVE = 0,
            DUAL_THR = 0,
            PREBLUR = 1,
            PREBLUR_K = 3,
            CLAHE = 0,
            GAMMA = 1.0,
            CONTRAST = 1.30,
            BRIGHT = 0,
            INVERT = 1,
            OPEN = 0,
            OPEN_ITERS = 0,
            CLOSE = 0,
            CLOSE_ITERS = 0,
            DILATE = 1,
            ERODE = 0,
            MAJORITY = 0,
            MAJORITY_ITERS = 0,
            UPSCALE = 3,
            GRAYSPACE = "Luma709",
            SAT_COLOR = 0.0,
            VAL_COLOR = 0.0,
            SAT_GRAY = 1.0,
            VAL_GRAY = 0.0,
            BINARIZER = "sauvola",
            SAUVOLA_WIN = 61,
            SAUVOLA_K = 0.34,
            SAUVOLA_R = 128
        };
    }
}
