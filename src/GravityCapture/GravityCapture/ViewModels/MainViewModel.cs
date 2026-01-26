using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GravityCapture.Models;
using GravityCapture.Services;

namespace GravityCapture.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsStore _store = new();
    private readonly ScreenCaptureService _capture = new();
    private readonly ApiClient _api = new();

    private readonly DispatcherTimer _saveTimer;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private AppSettings _settings = new();

    private DispatcherTimer? _timer;
    private bool _isRunning;
    private bool _isBusy;
    private CancellationTokenSource? _runCts;

    private BitmapSource? _previewImage;
    private string _lastResponse = "No requests yet.";
    private string _lastSendSummary = "Not started.";
    private string _statusText = "Idle";
    private string _busyText = "Idle";
    private string _previewOverlayText = "No preview yet.";
    private string _saveStatusText = "";

    public string Subtitle => "Tribe-log capture → OCR API (/ingest/screenshot)";
    public string RegionHint => "Tip: Open ARK, bring up the Tribe Log, then click Calibrate. Region is normalized, so it scales across resolutions.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SendOnceCommand { get; }
    public RelayCommand CalibrateRegionCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    public MainViewModel()
    {
        StartCommand = new RelayCommand(Start, () => !_isRunning);
        StopCommand = new RelayCommand(Stop, () => _isRunning);
        SendOnceCommand = new RelayCommand(() => _ = SendOnceAsync(manual: true), () => !_isBusy);
        CalibrateRegionCommand = new RelayCommand(OpenCalibrator, () => !_isBusy);
        SaveSettingsCommand = new RelayCommand(SaveSettingsNow);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            try
            {
                _store.Save(_settings);
                SaveStatusText = $"Auto-saved {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                SaveStatusText = $"Auto-save failed: {ex.Message}";
            }
        };
    }

    public void OnLoaded()
    {
        _settings = _store.Load();
        OnPropertyChanged(nameof(ApiBaseUrl));
        OnPropertyChanged(nameof(ServerName));
        OnPropertyChanged(nameof(TribeName));
        OnPropertyChanged(nameof(IntervalSecondsText));
        OnPropertyChanged(nameof(CriticalPingEnabled));
        OnPropertyChanged(nameof(UseExtractPreview));
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(UpscaleFactorText));
        OnPropertyChanged(nameof(RequestTimeoutSecondsText));
        UpdateComputed();
        SaveStatusText = $"Loaded settings: {_store.SettingsPath}";
    }

    public void OnClosing()
    {
        try { _store.Save(_settings); }
        catch { /* ignore */ }
        Stop();
    }

    public string ApiBaseUrl
    {
        get => _settings.ApiBaseUrl;
        set
        {
            _settings.ApiBaseUrl = value;
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    public string SharedSecret
    {
        get => _settings.SharedSecret;
        set
        {
            _settings.SharedSecret = value;
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    public string ServerName
    {
        get => _settings.ServerName;
        set { _settings.ServerName = value; ScheduleSave(); OnPropertyChanged(); }
    }

    public string TribeName
    {
        get => _settings.TribeName;
        set { _settings.TribeName = value; ScheduleSave(); OnPropertyChanged(); }
    }

    public bool CriticalPingEnabled
    {
        get => _settings.CriticalPingEnabled;
        set { _settings.CriticalPingEnabled = value; ScheduleSave(); OnPropertyChanged(); }
    }

    public bool UseExtractPreview
    {
        get => _settings.UseExtractPreview;
        set { _settings.UseExtractPreview = value; ScheduleSave(); OnPropertyChanged(); }
    }

    public string IntervalSecondsText
    {
        get => _settings.IntervalSeconds.ToString();
        set
        {
            if (int.TryParse(value, out var seconds))
                _settings.IntervalSeconds = Math.Clamp(seconds, 2, 3600);
            ScheduleSave();
            OnPropertyChanged();
            UpdateComputed();
        }
    }

    public string UpscaleFactorText
    {
        get => _settings.UpscaleFactor.ToString();
        set
        {
            if (int.TryParse(value, out var f))
                _settings.UpscaleFactor = Math.Clamp(f, 1, 4);
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    public string RequestTimeoutSecondsText
    {
        get => _settings.RequestTimeoutSeconds.ToString();
        set
        {
            if (int.TryParse(value, out var t))
                _settings.RequestTimeoutSeconds = Math.Clamp(t, 3, 300);
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    public string RegionText
    {
        get
        {
            var s = _settings.CaptureRegion?.ToString() ?? "Unset";
            if (!string.IsNullOrWhiteSpace(_settings.CaptureScreenDeviceName))
                s += $"  |  Screen: {_settings.CaptureScreenDeviceName}";
            return s;
        }
    }

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            _previewImage = value;
            OnPropertyChanged();
            PreviewOverlayText = _previewImage is null ? "No preview yet." : "";
        }
    }

    public string PreviewOverlayText
    {
        get => _previewOverlayText;
        private set { _previewOverlayText = value; OnPropertyChanged(); }
    }

    public string LastResponse
    {
        get => _lastResponse;
        private set { _lastResponse = value; OnPropertyChanged(); }
    }

    public string LastSendSummary
    {
        get => _lastSendSummary;
        private set { _lastSendSummary = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public string BusyText
    {
        get => _busyText;
        private set { _busyText = value; OnPropertyChanged(); }
    }

    public string SaveStatusText
    {
        get => _saveStatusText;
        private set { _saveStatusText = value; OnPropertyChanged(); }
    }

    private void SaveSettingsNow()
    {
        try
        {
            _store.Save(_settings);
            SaveStatusText = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SaveStatusText = $"Save failed: {ex.Message}";
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Start()
    {
        Stop(); // reset

        var seconds = Math.Clamp(_settings.IntervalSeconds, 2, 3600);

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += async (_, _) => await SendOnceAsync(manual: false);
        _timer.Start();

        _isRunning = true;
        StatusText = "Running";
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        UpdateComputed();

        // kick off immediately
        _ = SendOnceAsync(manual: false);
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;

        try { _runCts?.Cancel(); } catch { /* ignore */ }
        _runCts?.Dispose();
        _runCts = null;

        _isRunning = false;
        StatusText = "Idle";
        BusyText = "Idle";
        _isBusy = false;

        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        SendOnceCommand.RaiseCanExecuteChanged();
        CalibrateRegionCommand.RaiseCanExecuteChanged();
        UpdateComputed();
    }

    private void UpdateComputed()
    {
        var interval = Math.Clamp(_settings.IntervalSeconds, 2, 3600);
        _timer?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_timer != null)
                _timer.Interval = TimeSpan.FromSeconds(interval);
        }));

        // refresh region text
        OnPropertyChanged(nameof(RegionText));
    }

    private async Task SendOnceAsync(bool manual)
    {
        // Don't overlap sends; if a tick fires while a send is in-flight, skip that tick.
        if (!await _sendGate.WaitAsync(0))
            return;

        try
        {
            _isBusy = true;
            BusyText = manual ? "Sending (manual)..." : "Sending...";
            SendOnceCommand.RaiseCanExecuteChanged();
            CalibrateRegionCommand.RaiseCanExecuteChanged();

            var token = _runCts?.Token ?? CancellationToken.None;

            ScreenCaptureService.CaptureResult cap;
            try
            {
                cap = _capture.Capture(_settings);
                PreviewImage = cap.Preview;
                LastSendSummary = $"{DateTime.Now:HH:mm:ss}  |  Screen={cap.ScreenName}  Rect={cap.PixelRect.Width}x{cap.PixelRect.Height}  Upscale={Math.Clamp(_settings.UpscaleFactor,1,4)}x";
            }
            catch (Exception ex)
            {
                LastResponse = $"Capture failed: {ex.Message}";
                return;
            }

            // Optional debug: call /extract for preview. Otherwise ingest.
            string resp;
            if (_settings.UseExtractPreview)
                resp = await _api.ExtractAsync(cap.PngBytes, _settings, token);
            else
                resp = await _api.SendIngestScreenshotAsync(cap.PngBytes, _settings, token);

            LastResponse = resp;
            BusyText = "Idle";
        }
        finally
        {
            _isBusy = false;
            BusyText = _isRunning ? "Running" : "Idle";
            SendOnceCommand.RaiseCanExecuteChanged();
            CalibrateRegionCommand.RaiseCanExecuteChanged();
            _sendGate.Release();
        }
    }

    private void OpenCalibrator()
    {
        try
        {
            var wnd = new Views.RegionCalibratorWindow(_settings.CaptureRegion, _settings.CaptureScreenDeviceName);
            if (wnd.ShowDialog() == true)
            {
                _settings.CaptureRegion = wnd.ResultRegion;
                _settings.CaptureScreenDeviceName = wnd.ResultScreenDeviceName ?? "";
                ScheduleSave();
                OnPropertyChanged(nameof(RegionText));
            }
        }
        catch (Exception ex)
        {
            LastResponse = $"Calibrator failed: {ex.Message}";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
