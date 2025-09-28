using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GravityCapture
{
    public partial class MainWindow : Window
    {
        // ---------------- Settings store ----------------
        private sealed class AppSettings
        {
            public string? ChannelId { get; set; }
            public string? ApiUrl    { get; set; }
            public string? ApiKey    { get; set; }
            public string? Server    { get; set; }
            public string? Tribe     { get; set; }
        }

        private static readonly string SettingsDir  =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GravityCapture");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

        private readonly JsonSerializerOptions _json =
            new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        private AppSettings _settings = new();

        // ---------------- Ark detection ----------------
        private DispatcherTimer? _arkPoll;
        private IntPtr _arkHwnd = IntPtr.Zero;
        private string _arkTitle = "";

        public MainWindow()
        {
            InitializeComponent();
        }

        // ---------- lifecycle ----------
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettingsIntoUi();
            StartArkPolling();
            SetStatus("Ready.");
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            TrySaveFromUi();
        }

        // ---------- Settings load/save ----------
        private void LoadSettingsIntoUi()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    _settings = JsonSerializer.Deserialize<AppSettings>(json, _json) ?? new AppSettings();
                }
            }
            catch { /* keep defaults */ }

            ChannelBox.Text = _settings.ChannelId ?? "";
            ApiUrlBox.Text  = _settings.ApiUrl   ?? "";
            ApiKeyBox.Text  = _settings.ApiKey   ?? "";
            ServerBox.Text  = _settings.Server   ?? "";
            TribeBox.Text   = _settings.Tribe    ?? "";
        }

        private bool TrySaveFromUi()
        {
            try
            {
                _settings.ChannelId = ChannelBox.Text?.Trim();
                _settings.ApiUrl    = ApiUrlBox.Text?.Trim();
                _settings.ApiKey    = ApiKeyBox.Text?.Trim();
                _settings.Server    = ServerBox.Text?.Trim();
                _settings.Tribe     = TribeBox.Text?.Trim();

                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings, _json));
                return true;
            }
            catch (Exception ex)
            {
                SetStatus($"Save failed: {ex.Message}");
                return false;
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (TrySaveFromUi())
                SetStatus("Settings saved.");
        }

        // ---------- Ark polling ----------
        private void StartArkPolling()
        {
            _arkPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            _arkPoll.Tick += (_, __) => RefreshArkWindow();
            _arkPoll.Start();
        }

        private void RefreshArkWindow()
        {
            var (hwnd, title) = FindArkAscendedWindow();
            if (hwnd != _arkHwnd)
            {
                _arkHwnd = hwnd;
                _arkTitle = title;

                if (_arkHwnd == IntPtr.Zero)
                    SetStatus("No Ark window selected.");
                else
                    SetStatus($"Ark window: {title}");
            }

            // Enable/disable UI bits that need Ark
            SelectAreaBtn.IsEnabled = _arkHwnd != IntPtr.Zero;
            OcrCropBtn.IsEnabled    = _arkHwnd != IntPtr.Zero;
            OcrAndPostNowBtn.IsEnabled = _arkHwnd != IntPtr.Zero;
        }

        // Heuristics for ASA process/window
        private static (IntPtr hwnd, string title) FindArkAscendedWindow()
        {
            // 1) Try processes by common names
            string[] candidates = {
                "ArkAscended", "ArkAscendedClient", "ShooterGame", "ShooterGame-Win64-Shipping", "ArkAscendedClient-Win64-Shipping"
            };

            foreach (var name in candidates)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle))
                    {
                        var t = GetWindowText(p.MainWindowHandle);
                        if (!string.IsNullOrWhiteSpace(t))
                            return (p.MainWindowHandle, t);
                    }
                }
            }

            // 2) Fallback: enumerate windows and match title
            IntPtr hit = IntPtr.Zero;
            string title = "";
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                var wt = GetWindowText(h);
                if (string.IsNullOrWhiteSpace(wt)) return true;

                if (wt.Contains("ARK: Survival Ascended", StringComparison.OrdinalIgnoreCase) ||
                    wt.Contains("Ark Ascended", StringComparison.OrdinalIgnoreCase))
                {
                    hit = h; title = wt;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return (hit, title);
        }

        // ---------- Buttons ----------
        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (TrySaveFromUi())
                SetStatus("Started.");
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Stopped.");
        }

        private void SelectCropBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_arkHwnd == IntPtr.Zero)
            {
                SetStatus("No Ark window selected.");
                return;
            }

            try
            {
                // Assumes your RegionSelectorWindow takes the Ark HWND; adjust if your ctor differs
                var dlg = new Views.RegionSelectorWindow(_arkHwnd);
                dlg.Owner = this;
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                SetStatus($"Region selector failed: {ex.Message}");
            }
        }

        private async void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_arkHwnd == IntPtr.Zero) { SetStatus("No Ark window selected."); return; }
            await Task.Run(() =>
            {
                // place crop→OCR→paste pipeline here (client-side sample)
            });
            SetStatus("Crop → OCR complete.");
        }

        private async void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_arkHwnd == IntPtr.Zero) { SetStatus("No Ark window selected."); return; }
            await Task.Run(() =>
            {
                // place visible capture → OCR → post pipeline here
            });
            SetStatus("OCR & Post complete.");
        }

        private void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            // Existing implementation that posts LogLineBox.Text to your API
            SetStatus("Sent.");
        }

        // ---------- OCR overlay toggles / debug ----------
        private void ShowOcrDetailsCheck_Changed(object sender, RoutedEventArgs e)
        {
            OcrOverlay.Visibility = ShowOcrDetailsCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            // Wire up when OCR artifacts are generated (crop, binarized, json, settings)
            SetStatus("No debug artifacts yet.");
        }

        // ---------- helpers ----------
        private void SetStatus(string s)
        {
            StatusText.Text = s;
        }

        // Win32 helpers
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        private static string GetWindowText(IntPtr hWnd)
        {
            var sb = new System.Text.StringBuilder(512);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
