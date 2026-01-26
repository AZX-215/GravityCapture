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

    private AppSettings _settings = new();

    private readonly DispatcherTimer _saveTimer;

    private DispatcherTimer? _timer;
    private bool _isRunning;
    private bool _isBusy;
    private CancellationTokenSource? _cts;

    private BitmapSource? _previewImage;
    private string _lastResponse = "No requests yet.";
    private string _lastSendSummary = "Not started.";
    private string _statusText = "Idle";
    private string _busyText = "Idle";
    private string _previewOverlayText = "No preview yet.";

    public string Subtitle => "Tribe-log capture → OCR API (/ingest/screenshot)";
    public string RegionHint => "Tip: Open ARK, bring up the Tribe Log, then calibrate. Region is normalized, so it scales across resolutions.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SendOnceCommand { get; }
    public RelayCommand CalibrateRegionCommand { get; }

    public MainViewModel()
    {
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            try { _store.Save(_settings); } catch { /* ignore */ }
        };

        StartCommand = new RelayCommand(Start, () => !_isRunning);
        StopCommand = new RelayCommand(Stop, () => _isRunning);
        SendOnceCommand = new RelayCommand(async () => await SendOnceAsync(), () => !_isBusy);
        CalibrateRegionCommand = new RelayCommand(CalibrateRegion);

        UpdateComputed();
    }

    public void OnLoaded()
    {
        _settings = _store.Load();
        OnPropertyChanged(nameof(ApiBaseUrl));
        OnPropertyChanged(nameof(SharedSecret));
        OnPropertyChanged(nameof(ServerName));
        OnPropertyChanged(nameof(TribeName));
        OnPropertyChanged(nameof(IntervalSecondsText));
        OnPropertyChanged(nameof(CriticalPingEnabled));
        OnPropertyChanged(nameof(UseExtractPreview));
        OnPropertyChanged(nameof(RegionText));

        _lastSendSummary = $"Settings loaded from: {_store.SettingsPath}";
        OnPropertyChanged(nameof(LastSendSummary));
        UpdateComputed();
    }

    public void OnClosing()
    {
        try { _store.Save(_settings); } catch { /* ignore */ }
        Stop();
    }

    public string ApiBaseUrl
    {
        get => _settings.ApiBaseUrl;
        set { _settings.ApiBaseUrl = value ?? ""; ScheduleSave(); OnPropertyChanged(); }
    }

    public string SharedSecret
    {
        get => _settings.SharedSecret;
        set { _settings.SharedSecret = value ?? ""; ScheduleSave(); OnPropertyChanged(); }
    }

    public string ServerName
    {
        get => _settings.ServerName;
        set { _settings.ServerName = value ?? "unknown"; ScheduleSave(); OnPropertyChanged(); }
    }

    public string TribeName
    {
        get => _settings.TribeName;
        set { _settings.TribeName = value ?? "unknown"; ScheduleSave(); OnPropertyChanged(); }
    }

    public string IntervalSecondsText
    {
        get => _settings.IntervalSeconds.ToString();
        set
        {
            if (int.TryParse(value, out var n))
                _settings.IntervalSeconds = Math.Clamp(n, 2, 3600);
            ScheduleSave(); OnPropertyChanged();
        }
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

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Start()
    {
        Stop(); // reset

        var seconds = Math.Clamp(_settings.IntervalSeconds, 2, 3600);

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(seconds)
        };
        _timer.Tick += async (_, _) => await SendOnceAsync();
        _timer.Start();

        _isRunning = true;
        StatusText = "Running";
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        UpdateComputed();
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;

        _cts?.Cancel();
        _cts = null;

        _isRunning = false;
        StatusText = "Idle";
        BusyText = "Idle";
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        UpdateComputed();
    }

    private async Task SendOnceAsync()
    {
        if (_isBusy)
            return;

        _isBusy = true;
        BusyText = "Sending…";
        SendOnceCommand.RaiseCanExecuteChanged();
        UpdateComputed();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var cap = _capture.Capture(_settings);
            PreviewImage = cap.Preview;

            // persist last-used screen if available
            _settings.CaptureScreenDeviceName = cap.ScreenName;
            ScheduleSave();
            OnPropertyChanged(nameof(RegionText));

            var t0 = DateTime.Now;
            
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(TimeSpan.FromSeconds(60));
            var sendToken = sendCts.Token;
var resp = await _api.SendIngestScreenshotAsync(cap.PngBytes, _settings, sendToken);
            var msg = resp;

            if (_settings.UseExtractPreview)
            {
                var ext = await _api.ExtractAsync(cap.PngBytes, _settings, sendToken);
                msg = $"INGEST:\n{resp}\n\nEXTRACT:\n{ext}";
            }

            LastResponse = msg;            var elapsed = DateTime.Now - t0;

            LastSendSummary = $"Last send: {t0:HH:mm:ss}  |  Region: {cap.PixelRect.Width}x{cap.PixelRect.Height}  |  Screen: {cap.ScreenName}  |  Ping: {(Settings.CriticalPingEnabled ? "on" : "off")}  |  Took: {elapsed.TotalMilliseconds:0} ms";
            StatusText = _isRunning ? "Running" : "Idle";
        }
                catch (TaskCanceledException)
        {
            StatusText = "Timed out";
            LastResponse = "Timeout after 60s (no response from API).";
        }

catch (OperationCanceledException)
        {
            LastResponse = "Canceled.";
        }
        catch (Exception ex)
        {
            StatusText = "Error";
            LastResponse = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            BusyText = "Idle";
            SendOnceCommand.RaiseCanExecuteChanged();
            UpdateComputed();
        }
    }

    private void CalibrateRegion()
    {
        try
        {
            var w = new Views.RegionCalibratorWindow(_settings.CaptureRegion, _settings.CaptureScreenDeviceName);
            w.Owner = System.Windows.Application.Current?.MainWindow;
            var ok = w.ShowDialog() ?? false;
            if (!ok)
                return;

            _settings.CaptureRegion = w.SelectedRegion ?? _settings.CaptureRegion;
            _settings.CaptureScreenDeviceName = w.SelectedScreenDeviceName ?? _settings.CaptureScreenDeviceName;
            _settings.CaptureRegion.Clamp();
            ScheduleSave();

            OnPropertyChanged(nameof(RegionText));
            LastSendSummary = "Region calibrated.";
        }
        catch (Exception ex)
        {
            LastSendSummary = $"Calibration failed: {ex.Message}";
        }
    }

    private void UpdateComputed()
    {
        StatusText = _isRunning ? StatusText : StatusText;
        BusyText = _isBusy ? BusyText : BusyText;
        OnPropertyChanged(nameof(BusyText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RegionText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}