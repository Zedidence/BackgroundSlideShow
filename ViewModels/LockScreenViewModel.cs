using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackgroundSlideShow.Models;
using BackgroundSlideShow.Services;

namespace BackgroundSlideShow.ViewModels;

public partial class LockScreenViewModel : ObservableObject, IDisposable
{
    private readonly LockScreenEngine _engine;
    private readonly AppSettings      _appSettings;

    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private string _statusText = "Stopped";
    [ObservableProperty] private int    _imageCount;
    [ObservableProperty] private string _currentImageName = "";

    // ── Settings passthroughs ─────────────────────────────────────────────────

    public string FolderPath
    {
        get => _appSettings.LockScreenFolderPath;
        set
        {
            if (_appSettings.LockScreenFolderPath == value) return;
            _appSettings.LockScreenFolderPath = value;
            _appSettings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFolder));
        }
    }

    public bool HasFolder =>
        !string.IsNullOrEmpty(FolderPath) && Directory.Exists(FolderPath);

    public bool CollageEnabled
    {
        get => _appSettings.LockScreenCollageEnabled;
        set
        {
            if (_appSettings.LockScreenCollageEnabled == value) return;
            _appSettings.LockScreenCollageEnabled = value;
            _appSettings.Save();
            OnPropertyChanged();
        }
    }

    public int IntervalMinutes
    {
        get => _appSettings.LockScreenIntervalMinutes;
        set
        {
            if (_appSettings.LockScreenIntervalMinutes == value) return;
            _appSettings.LockScreenIntervalMinutes = value;
            _appSettings.Save();
            OnPropertyChanged();
        }
    }

    public FitMode FitMode
    {
        get => _appSettings.LockScreenFitMode;
        set
        {
            if (_appSettings.LockScreenFitMode == value) return;
            _appSettings.LockScreenFitMode = value;
            _appSettings.Save();
            OnPropertyChanged();
        }
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public LockScreenViewModel(LockScreenEngine engine, AppSettings appSettings)
    {
        _engine      = engine;
        _appSettings = appSettings;
        _engine.StateChanged += OnEngineStateChanged;
        SyncState();
    }

    private void OnEngineStateChanged(object? sender, EventArgs e) =>
        Application.Current.Dispatcher.Invoke(SyncState);

    private void SyncState()
    {
        IsRunning        = _engine.IsRunning;
        ImageCount       = _engine.ImageCount;
        CurrentImageName = _engine.CurrentFileName;

        StatusText = _engine.IsRunning
            ? $"{_engine.CurrentFileName}  ({_engine.ImageCount} image{(_engine.ImageCount == 1 ? "" : "s")} in rotation)"
            : "Stopped — press Start to begin.";
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task Start()
    {
        await _engine.StartAsync();
        if (!_engine.IsRunning)
            StatusText = "No supported image files found in the selected folder.";
    }

    [RelayCommand]
    private void Stop() => _engine.Stop();

    [RelayCommand]
    private async Task Next() => await _engine.NextAsync();

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select Lock Screen Image Folder" };
        if (dialog.ShowDialog() == true)
            FolderPath = dialog.FolderName;
    }

    // Bug 3 fix: unsubscribe from engine event so the engine doesn't hold this VM alive.
    public void Dispose() => _engine.StateChanged -= OnEngineStateChanged;
}
