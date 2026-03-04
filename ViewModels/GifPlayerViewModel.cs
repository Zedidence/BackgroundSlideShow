using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackgroundSlideShow.Services;

namespace BackgroundSlideShow.ViewModels;

public partial class GifPlayerViewModel : ObservableObject, IDisposable
{
    private readonly GifPlayerEngine _engine;
    private readonly AppSettings     _appSettings;

    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private string _statusText   = "Stopped";
    [ObservableProperty] private int    _gifCount;
    [ObservableProperty] private string _currentGifName = "";

    // ── Settings exposed as passthrough to AppSettings ────────────────────────

    public string GifFolderPath
    {
        get => _appSettings.GifFolderPath;
        set
        {
            if (_appSettings.GifFolderPath == value) return;
            _appSettings.GifFolderPath = value;
            _appSettings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFolder));
        }
    }

    /// <summary>True when GifFolderPath is set and the directory exists.</summary>
    public bool HasFolder =>
        !string.IsNullOrEmpty(GifFolderPath) && Directory.Exists(GifFolderPath);

    public int SecondsPerFile
    {
        get => _appSettings.GifSecondsPerFile;
        set
        {
            if (_appSettings.GifSecondsPerFile == value) return;
            _appSettings.GifSecondsPerFile = value;
            _appSettings.Save();
            OnPropertyChanged();
        }
    }

    // ── Construction ─────────────────────────────────────────────────────────

    public GifPlayerViewModel(GifPlayerEngine engine, AppSettings appSettings)
    {
        _engine      = engine;
        _appSettings = appSettings;
        _engine.StateChanged += OnEngineStateChanged;
        SyncState();
    }

    private void OnEngineStateChanged(object? sender, EventArgs e) => SyncState();

    private void SyncState()
    {
        IsRunning      = _engine.IsRunning;
        GifCount       = _engine.GifCount;
        CurrentGifName = _engine.CurrentFileName;

        StatusText = _engine.IsRunning
            ? $"{_engine.CurrentFileName}  ({_engine.CurrentIndex + 1} / {_engine.GifCount})"
            : "Stopped — press Start to begin.";
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Start()
    {
        _engine.Start();
        if (!_engine.IsRunning)
            StatusText = "No .gif files found in the selected folder.";
    }

    [RelayCommand]
    private void Stop() => _engine.Stop();

    [RelayCommand]
    private void Next() => _engine.Next();

    [RelayCommand]
    private void Prev() => _engine.Prev();

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select GIF Folder" };
        if (dialog.ShowDialog() == true)
            GifFolderPath = dialog.FolderName;
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.Dispose();
    }
}
