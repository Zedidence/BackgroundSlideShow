using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackgroundSlideShow.Models;
using BackgroundSlideShow.Services;

namespace BackgroundSlideShow.ViewModels;

public partial class MonitorViewModel : ObservableObject, IDisposable
{
    private readonly SlideshowEngine _engine;
    private readonly ILibraryService _libraryService;

    public string MonitorId { get; }

    [ObservableProperty] private MonitorConfig _config;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private System.Windows.Rect _bounds;
    [ObservableProperty] private bool _isPrimary;
    [ObservableProperty] private bool _isSelected;

    // Slideshow state
    [ObservableProperty] private SlideshowStatus _status = SlideshowStatus.Stopped;
    [ObservableProperty] private string _currentImagePath = string.Empty;
    [ObservableProperty] private string _nextChangeCountdown = "--:--";

    // Computed display helpers
    public bool IsPortrait => Bounds.Height > Bounds.Width;
    public string OrientationLabel => IsPortrait ? "Portrait" : "Landscape";

    public string PoolDescription => Config.ImagePoolMode switch
    {
        Models.ImagePoolMode.Landscape => "Landscape Only",
        Models.ImagePoolMode.Portrait  => "Portrait Only",
        Models.ImagePoolMode.Smart     => IsPortrait ? "Smart — Portrait" : "Smart — Landscape",
        _                              => "All Images",
    };

    public string CurrentImageName => string.IsNullOrEmpty(CurrentImagePath)
        ? string.Empty
        : System.IO.Path.GetFileName(CurrentImagePath);

    // ── Folder assignment ─────────────────────────────────────────────────────

    /// <summary>All library folders with their per-monitor assignment state.</summary>
    public ObservableCollection<FolderAssignmentItemViewModel> FolderAssignments { get; } = new();

    private System.Timers.Timer? _countdownTimer;

    public MonitorViewModel(MonitorInfo monitor, MonitorConfig config, SlideshowEngine engine,
                            ILibraryService libraryService, int index)
    {
        _engine          = engine;
        _libraryService  = libraryService;
        _config          = config;
        MonitorId        = monitor.DeviceId;
        DisplayName      = monitor.IsPrimary ? $"Monitor {index} (Primary)" : $"Monitor {index}";
        Bounds           = monitor.Bounds;
        IsPrimary        = monitor.IsPrimary;

        Config.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MonitorConfig.ImagePoolMode))
                OnPropertyChanged(nameof(PoolDescription));

            if (e.PropertyName == nameof(MonitorConfig.FolderAssignmentMode))
                ApplyFolderAssignmentToEngine();
        };

        // Load folder assignments asynchronously; refresh when library changes
        _ = LoadFolderAssignmentsAsync();
        _libraryService.LibraryChanged += OnLibraryChanged;

        // Countdown refresh every second
        _countdownTimer = new System.Timers.Timer(1000);
        _countdownTimer.Elapsed += (_, _) => RefreshCountdown();
        _countdownTimer.AutoReset = true;
        _countdownTimer.Start();
    }

    private async void OnLibraryChanged(object? sender, EventArgs e) =>
        await LoadFolderAssignmentsAsync();

    // ── Folder assignment helpers ──────────────────────────────────────────────

    private async Task LoadFolderAssignmentsAsync()
    {
        var folders         = await _libraryService.GetFoldersWithCountAsync();
        var assignedIds     = (await _libraryService.GetFolderAssignmentsAsync(Config.Id)).ToHashSet();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // Unsubscribe old items
            foreach (var item in FolderAssignments)
                item.PropertyChanged -= OnFolderAssignmentItemChanged;

            FolderAssignments.Clear();

            foreach (var (folder, _) in folders)
            {
                var item = new FolderAssignmentItemViewModel(folder, assignedIds.Contains(folder.Id));
                item.PropertyChanged += OnFolderAssignmentItemChanged;
                FolderAssignments.Add(item);
            }
        });
    }

    private void OnFolderAssignmentItemChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FolderAssignmentItemViewModel.IsAssigned)) return;
        ApplyFolderAssignmentToEngine();
        _ = SaveFolderAssignmentsAsync();
    }

    private void ApplyFolderAssignmentToEngine()
    {
        _engine.SetFolderAssignments(MonitorId, GetAllowedFolderIds());
    }

    private async Task SaveFolderAssignmentsAsync()
    {
        var assigned = FolderAssignments
            .Where(f => f.IsAssigned)
            .Select(f => f.Folder.Id);
        await _libraryService.SetFolderAssignmentsAsync(Config.Id, assigned);
    }

    /// <summary>
    /// Returns the effective allowed folder set for the engine:
    /// null when FolderAssignmentMode = All, a HashSet when = Selected.
    /// </summary>
    private IReadOnlySet<int>? GetAllowedFolderIds()
    {
        if (Config.FolderAssignmentMode != FolderAssignmentMode.Selected)
            return null;

        return FolderAssignments
            .Where(f => f.IsAssigned)
            .Select(f => f.Folder.Id)
            .ToHashSet();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Start() => _engine.Start(Config, GetAllowedFolderIds());

    [RelayCommand]
    private void Pause() => _engine.Pause(MonitorId);

    [RelayCommand]
    private void Resume() => _engine.Resume(MonitorId);

    [RelayCommand]
    private void Skip() => _engine.Skip(MonitorId);

    [RelayCommand]
    private void Stop() => _engine.Stop(MonitorId);

    partial void OnCurrentImagePathChanged(string value) =>
        OnPropertyChanged(nameof(CurrentImageName));

    public void UpdateState(SlideshowState state)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            Status = state.Status;
            CurrentImagePath = state.CurrentImage?.FilePath ?? string.Empty;
        });
    }

    public void Dispose()
    {
        _libraryService.LibraryChanged -= OnLibraryChanged;

        foreach (var item in FolderAssignments)
            item.PropertyChanged -= OnFolderAssignmentItemChanged;

        _countdownTimer?.Stop();
        _countdownTimer?.Dispose();
        _countdownTimer = null;
    }

    private void RefreshCountdown()
    {
        var state = _engine.GetState(MonitorId);
        if (state?.NextChangeAt is null)
        {
            NextChangeCountdown = "--:--";
            return;
        }
        var remaining = state.TimeUntilNext;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        NextChangeCountdown = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
    }
}
