using System.Collections.Concurrent;
using BackgroundSlideShow.Models;
using BackgroundSlideShow;

namespace BackgroundSlideShow.Services;

/// <summary>Manages per-monitor slideshow timers and orchestrates wallpaper changes.</summary>
public class SlideshowEngine : IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly WallpaperService _wallpaperService;
    private readonly ILibraryService _libraryService;
    private readonly ImageSelectorService _imageSelector;
    private readonly AppSettings _appSettings;

    // Per-monitor state — ConcurrentDictionary for thread-safe access from UI + timer threads
    private readonly ConcurrentDictionary<string, PerMonitorState> _states = new();

    public event EventHandler<SlideshowStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Optional delegate called on the UI thread when a wallpaper is about to change.
    /// Receives (oldImagePath, monitorBounds, durationMs). Used by the transition overlay.
    /// Wired up in App.xaml.cs so the engine stays decoupled from WPF windows.
    /// </summary>
    public Action<string, System.Windows.Rect, int>? ShowTransitionOverlay { get; set; }

    public SlideshowEngine(
        MonitorService monitorService,
        WallpaperService wallpaperService,
        ILibraryService libraryService,
        ImageSelectorService imageSelector,
        AppSettings appSettings)
    {
        _monitorService = monitorService;
        _wallpaperService = wallpaperService;
        _libraryService = libraryService;
        _imageSelector = imageSelector;
        _appSettings = appSettings;

        _libraryService.LibraryChanged += (_, _) => RefreshImagePool();
    }

    // ── Public control ────────────────────────────────────────────────────────

    /// <param name="allowedFolderIds">
    /// Pre-computed folder whitelist. Pass null when FolderAssignmentMode = All.
    /// </param>
    public void Start(MonitorConfig config, IReadOnlySet<int>? allowedFolderIds = null)
    {
        var monitorId = config.MonitorId;
        Stop(monitorId);

        var monitor = _monitorService.GetMonitors()
            .FirstOrDefault(m => m.DeviceId == monitorId);
        if (monitor is null) return;

        var state = new PerMonitorState(monitor, config) { AllowedFolderIds = allowedFolderIds };
        state.Timer = new System.Timers.Timer(config.IntervalSeconds * 1000.0);
        state.Timer.Elapsed += (_, _) => AdvanceMonitor(state);
        state.Timer.AutoReset = true;
        _states[monitorId] = state;

        // Show first image immediately (no transition — there is no "old" image yet)
        AdvanceMonitor(state);
        state.Timer.Start();

        RaiseStateChanged(state.SlideshowState);
    }

    public void Pause(string monitorId)
    {
        if (!_states.TryGetValue(monitorId, out var state)) return;
        state.Timer?.Stop();
        state.SlideshowState.Status = SlideshowStatus.Paused;
        state.SlideshowState.NextChangeAt = null;
        RaiseStateChanged(state.SlideshowState);
    }

    public void Resume(string monitorId)
    {
        if (!_states.TryGetValue(monitorId, out var state)) return;
        state.Timer?.Start();
        state.SlideshowState.Status = SlideshowStatus.Playing;
        state.SlideshowState.NextChangeAt = DateTime.UtcNow.AddSeconds(state.Config.IntervalSeconds);
        RaiseStateChanged(state.SlideshowState);
    }

    public void Skip(string monitorId)
    {
        if (!_states.TryGetValue(monitorId, out var state)) return;
        state.Timer?.Stop();
        AdvanceMonitor(state, suppressStateChanged: true);
        state.Timer?.Start();
        state.SlideshowState.NextChangeAt = DateTime.UtcNow.AddSeconds(state.Config.IntervalSeconds);
        RaiseStateChanged(state.SlideshowState);
    }

    public void Stop(string monitorId)
    {
        if (!_states.TryGetValue(monitorId, out var state)) return;
        state.Timer?.Stop();
        state.Timer?.Dispose();
        state.SlideshowState.Status = SlideshowStatus.Stopped;
        state.SlideshowState.NextChangeAt = null;
        _states.TryRemove(monitorId, out _);
        RaiseStateChanged(state.SlideshowState);
    }

    public void StopAll()
    {
        foreach (var id in _states.Keys.ToList())
            Stop(id);
    }

    public SlideshowState? GetState(string monitorId) =>
        _states.TryGetValue(monitorId, out var s) ? s.SlideshowState : null;

    /// <summary>Updates the timer interval live when the user changes IntervalSeconds while a slideshow is running.</summary>
    public void UpdateConfig(MonitorConfig config)
    {
        if (_states.TryGetValue(config.MonitorId, out var state) && state.Timer is not null)
            state.Timer.Interval = config.IntervalSeconds * 1000.0;
    }

    /// <summary>
    /// Updates the allowed folder set for an actively running monitor.
    /// Forces an immediate deck rebuild on the next advance so the new assignment takes effect.
    /// </summary>
    public void SetFolderAssignments(string monitorId, IReadOnlySet<int>? allowedFolderIds)
    {
        if (!_states.TryGetValue(monitorId, out var state)) return;
        lock (state.AdvanceLock)
        {
            state.AllowedFolderIds = allowedFolderIds;
            state.PoolVersion = -1; // force deck rebuild on next advance
        }
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void AdvanceMonitor(PerMonitorState state, bool suppressStateChanged = false)
    {
        // Lock per-state so timer thread and UI thread (Skip) can't race on the deck index.
        lock (state.AdvanceLock)
        {
            var images = _cachedImages;
            if (images.Count == 0) return;

            // Rebuild the shuffle deck when the image pool has changed or the deck is exhausted.
            if (state.PoolVersion != _poolVersion || state.DeckIndex >= state.ShuffleDeck.Count)
            {
                var lastPath = state.SlideshowState.CurrentImage?.FilePath;

                state.ShuffleDeck = _imageSelector.BuildShuffledDeck(
                    images, state.Monitor, state.Config, state.AllowedFolderIds);
                state.DeckIndex = 0;
                state.PoolVersion = _poolVersion;

                if (lastPath != null && state.ShuffleDeck.Count > 1
                    && state.ShuffleDeck[0].FilePath.Equals(lastPath, StringComparison.OrdinalIgnoreCase))
                {
                    int swapIdx = Random.Shared.Next(1, state.ShuffleDeck.Count);
                    (state.ShuffleDeck[0], state.ShuffleDeck[swapIdx]) =
                        (state.ShuffleDeck[swapIdx], state.ShuffleDeck[0]);
                }
            }

            if (state.ShuffleDeck.Count == 0) return;

            var next = state.ShuffleDeck[state.DeckIndex++];

            if (string.IsNullOrEmpty(state.Monitor.WallpaperDevicePath))
            {
                AppLogger.Error($"Monitor {state.Monitor.DeviceId} has no wallpaper device path — skipping wallpaper set");
                return;
            }

            // If transitions are enabled and there is a currently-displayed image, post a
            // request to the UI thread to show the fade overlay BEFORE we set the new wallpaper.
            // The overlay covers the monitor with the old image and fades to transparent,
            // revealing the new wallpaper underneath.
            var oldImagePath = state.SlideshowState.CurrentImage?.FilePath;
            if (_appSettings.TransitionsEnabled
                && !string.IsNullOrEmpty(oldImagePath)
                && !state.Monitor.Bounds.IsEmpty
                && ShowTransitionOverlay is not null)
            {
                var bounds   = state.Monitor.Bounds;
                var duration = _appSettings.TransitionDurationMs;
                var oldPath  = oldImagePath!;
                // Show the overlay on the UI thread.
                System.Windows.Application.Current.Dispatcher.Invoke(
                    () => ShowTransitionOverlay(oldPath, bounds, duration));
                // Flush the WPF render queue so the overlay window is actually painted
                // before SetWallpaper runs — prevents a flash of the new wallpaper.
                System.Windows.Application.Current.Dispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Render, (Action)(() => { }));
            }

            try
            {
                _wallpaperService.SetWallpaper(state.Monitor.WallpaperDevicePath, next.FilePath, state.Config.FitMode);
            }
            catch (Exception ex) { AppLogger.Error($"SetWallpaper failed for monitor {state.Monitor.DeviceId}", ex); return; }

            state.SlideshowState.CurrentImage = next;
            state.SlideshowState.Status = SlideshowStatus.Playing;
            state.SlideshowState.NextChangeAt = DateTime.UtcNow.AddSeconds(state.Config.IntervalSeconds);

            if (!suppressStateChanged)
                RaiseStateChanged(state.SlideshowState);
        }
    }

    private volatile IReadOnlyList<Models.ImageEntry> _cachedImages = Array.Empty<Models.ImageEntry>();
    private CancellationTokenSource? _poolRefreshCts;
    private volatile int _poolVersion = 0;

    private void RefreshImagePool()
    {
        _poolRefreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _poolRefreshCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                var images = await _libraryService.GetAllImagesAsync(cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    _cachedImages = images;
                    Interlocked.Increment(ref _poolVersion);
                }
            }
            catch (OperationCanceledException) { /* superseded by a newer refresh */ }
        }, cts.Token);
    }

    public async Task LoadImagePoolAsync()
    {
        _cachedImages = await _libraryService.GetAllImagesAsync();
        Interlocked.Increment(ref _poolVersion);
    }

    private void RaiseStateChanged(SlideshowState state) =>
        StateChanged?.Invoke(this, new SlideshowStateChangedEventArgs(state));

    public void Dispose()
    {
        _poolRefreshCts?.Cancel();
        _poolRefreshCts?.Dispose();
        foreach (var s in _states.Values)
        {
            s.Timer?.Stop();
            s.Timer?.Dispose();
        }
        _states.Clear();
    }

    // ── Nested types ──────────────────────────────────────────────────────────

    private class PerMonitorState
    {
        public MonitorInfo Monitor { get; }
        public MonitorConfig Config { get; }
        public System.Timers.Timer? Timer { get; set; }
        public List<ImageEntry> ShuffleDeck { get; set; } = new();
        public int DeckIndex { get; set; } = 0;
        public int PoolVersion { get; set; } = -1;
        public SlideshowState SlideshowState { get; }
        public object AdvanceLock { get; } = new();

        /// <summary>
        /// Null = all folders. Non-null = only images from these folder IDs are eligible.
        /// Updated live via <see cref="SlideshowEngine.SetFolderAssignments"/>.
        /// </summary>
        public IReadOnlySet<int>? AllowedFolderIds { get; set; }

        public PerMonitorState(MonitorInfo monitor, MonitorConfig config)
        {
            Monitor = monitor;
            Config = config;
            SlideshowState = new SlideshowState { MonitorId = monitor.DeviceId };
        }
    }
}

public class SlideshowStateChangedEventArgs : EventArgs
{
    public SlideshowState State { get; }
    public SlideshowStateChangedEventArgs(SlideshowState state) => State = state;
}
