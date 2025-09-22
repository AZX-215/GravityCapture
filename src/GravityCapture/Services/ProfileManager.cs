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

        // Serialize omitting nulls so missing keys stay missing on disk.
        private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
            void SetI(string k, int v) => Set(k, v.ToString(inv));
            void SetD(string k, double v) => Set(k, v.ToString(inv));
            void SetB(string k, int v) => Set(k, v != 0 ? "1" : "0");
            void Unset(string k) => Environment.SetEnvironmentVariable(k, null);

            void SetIIf(string k, int? v) { if (v.HasValue) SetI(k, v.Value); else Unset(k); }
            void SetDIf(string k, double? v) { if (v.HasValue) SetD(k, v.Value); else Unset(k); }
            void SetBIf(string k, int? v) { if (v.HasValue) SetB(k, v.Value); else Unset(k); }
            void SetSIf(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) Set(k, v); else Unset(k); }

            // Core pipeline knobs
            SetBIf("GC_OCR_TONEMAP", p.TONEMAP);
            SetBIf("GC_OCR_ADAPTIVE", p.ADAPTIVE);
            SetIIf("GC_OCR_ADAPTIVE_WIN", p.ADAPTIVE_WIN);
            SetIIf("GC_OCR_ADAPTIVE_C", p.ADAPTIVE_C);

            // Dual threshold + explicit thresholds
            SetBIf("GC_OCR_DUAL_THR", p.DUAL_THR);
            SetIIf("GC_OCR_THR_LOW", p.THR_LOW);
            SetIIf("GC_OCR_THR_HIGH", p.THR_HIGH);

            // Morphology + geometric
            SetBIf("GC_OCR_OPEN", p.OPEN);
            SetIIf("GC_OCR_OPEN_ITERS", p.OPEN_ITERS);
            SetBIf("GC_OCR_CLOSE", p.CLOSE);
            SetIIf("GC_OCR_CLOSE_ITERS", p.CLOSE_ITERS);
            SetIIf("GC_OCR_DILATE", p.DILATE);
            SetIIf("GC_OCR_ERODE", p.ERODE);

            // Photometric
            SetDIf("GC_OCR_CONTRAST", p.CONTRAST);
            SetDIf("GC_OCR_BRIGHT", p.BRIGHT);
            SetBIf("GC_OCR_INVERT", p.INVERT);
            SetBIf("GC_OCR_PREBLUR", p.PREBLUR);
            SetIIf("GC_OCR_PREBLUR_K", p.PREBLUR_K);
            SetBIf("GC_OCR_CLAHE", p.CLAHE);
            SetDIf("GC_OCR_GAMMA", p.GAMMA);

            // Majority
            SetBIf("GC_OCR_MAJORITY", p.MAJORITY);
            SetIIf("GC_OCR_MAJORITY_ITERS", p.MAJORITY_ITERS);

            // Upscale
            SetIIf("GC_OCR_UPSCALE", p.UPSCALE);

            // HSV gates
            SetDIf("GC_OCR_SAT_COLOR", p.SAT_COLOR);
            SetDIf("GC_OCR_VAL_COLOR", p.VAL_COLOR);
            SetDIf("GC_OCR_SAT_GRAY", p.SAT_GRAY);
            SetDIf("GC_OCR_VAL_GRAY", p.VAL_GRAY);

            // Gray space + binarizers
            SetSIf("GC_OCR_GRAYSPACE", p.GRAYSPACE);
            SetSIf("GC_OCR_BINARIZER", p.BINARIZER);
            SetIIf("GC_OCR_SAUVOLA_WIN", p.SAUVOLA_WIN);
            SetDIf("GC_OCR_SAUVOLA_K", p.SAUVOLA_K);
            SetIIf("GC_OCR_SAUVOLA_R", p.SAUVOLA_R);
            SetDIf("GC_OCR_WOLF_K", p.WOLF_K);
            SetDIf("GC_OCR_WOLF_P", p.WOLF_P);

            
            // Capture-stage toggles
            SetBIf("GC_CAPTURE_TONEBOOST", p.CAPTURE_TONEBOOST);
// Distance thicken + fill/cleanup
            SetBIf("GC_OCR_DISTANCE_THICKEN", p.DISTANCE_THICKEN);
            SetDIf("GC_OCR_DISTANCE_R", p.DISTANCE_R);
            SetBIf("GC_OCR_FILL_HOLES", p.FILL_HOLES);
            SetIIf("GC_OCR_FILL_HOLES_MAX", p.FILL_HOLES_MAX);
            SetIIf("GC_OCR_REMOVE_DOTS_MAXAREA", p.REMOVE_DOTS_MAXAREA);

            // Do not hard-force SDR/HDR-specific overrides here.
            // Profiles control all behavior. Unset any legacy keys if absent.
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
            UPSCALE = 2,
            CAPTURE_TONEBOOST = 1
        };

        private static OcrProfile BuildDefaultSdr() => new()
        {
            TONEMAP = 0,
            ADAPTIVE = 0,
            ADAPTIVE_WIN = 0,
            ADAPTIVE_C = 0,
            SHARPEN = 1,
            DUAL_THR = 0,
            PREBLUR = 0,
            PREBLUR_K = 1,
            CLAHE = 0,
            GAMMA = 1.0,
            CONTRAST = 1.40,
            BRIGHT = 1,
            INVERT = 1,
            OPEN = 1,
            OPEN_ITERS = 1,
            CLOSE = 1,
            CLOSE_ITERS = 1,
            DILATE = 0,
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
            SAUVOLA_WIN = 35,
            SAUVOLA_K = 0.32,
            SAUVOLA_R = 128
        };
    }
}
