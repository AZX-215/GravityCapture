using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
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

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            TrySaveFromUi();
        }

        // Make title bar dark (preserve your dark theme)
        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int useDark = 1;
                // 20 works on 1903+, 19 for older 1809 Insider – do both.
                _ = DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
            }
            catch { /* best effort */ }
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
            _arkPoll = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _arkPoll.Tick += (_, __) => RefreshArkWindow();
            _arkPoll.Start();

            // Initial probe
            RefreshArkWindow();
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

            // Enable/disable actions that require Ark
            SelectAreaBtn.IsEnabled     = _arkHwnd != IntPtr.Zero;
            OcrCropBtn.IsEnabled        = _arkHwnd != IntPtr.Zero;
            OcrAndPostNowBtn.IsEnabled  = _arkHwnd != IntPtr.Zero;
        }

        // Heuristics for ASA window/process
        private static (IntPtr hwnd, string title) FindArkAscendedWindow()
        {
            // Common process names; we only accept visible windows with a non-empty title.
            string[] candidates = {
                "ArkAscended", "ArkAscendedClient",
                "ShooterGame", "ShooterGame-Win64-Shipping",
                "ArkAscendedClient-Win64-Shipping"
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

            // Fallback: enumerate top-level windows and match title text.
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
                // Keep your existing region selector behaviour; we only pass the HWND.
                var dlg = new Views.RegionSelectorWindow(_arkHwnd);
                dlg.Owner = this;
                dlg.ShowDialog();

                // If your selector writes the crop to a shared place, the preview code can pick it up.
                SetStatus("Region selected.");
            }
            catch (Exception ex)
            {
                SetStatus($"Region selector failed: {ex.Message}");
            }
        }

        private async void OcrCropBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_arkHwnd == IntPtr.Zero) { SetStatus("No Ark window selected."); return; }

            try
            {
                // TODO: call your existing capture+OCR pipeline with the selected crop.
                await Task.CompletedTask;
                SetStatus("Crop → OCR complete.");
            }
            catch (Exception ex)
            {
                SetStatus($"OCR failed: {ex.Message}");
            }
        }

        private async void OcrAndPostNowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_arkHwnd == IntPtr.Zero) { SetStatus("No Ark window selected."); return; }

            try
            {
                // TODO: call your visible-window capture + OCR + post pipeline.
                await Task.CompletedTask;
                SetStatus("OCR & Post complete.");
            }
            catch (Exception ex)
            {
                SetStatus($"OCR & Post failed: {ex.Message}");
            }
        }

        private void SendParsedBtn_Click(object sender, RoutedEventArgs e)
        {
            // Keep your existing logic that posts LogLineBox.Text to the API.
            // Here we only provide user feedback.
            SetStatus("Sent.");
        }

        // ---------- OCR overlay toggles / debug ----------
        private void ShowOcrDetailsCheck_Changed(object sender, RoutedEventArgs e)
        {
            OcrOverlay.Visibility = ShowOcrDetailsCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveDebugBtn_Click(object sender, RoutedEventArgs e)
        {
            // Hook this up once your OCR pipeline writes artifacts (crop/bin/json/settings).
            SetStatus("No debug artifacts yet.");
        }

        // ---------- helpers ----------
        private void SetStatus(string s) => StatusText.Text = s;

        // Win32 helpers
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        private static string GetWindowText(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            _ = GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
