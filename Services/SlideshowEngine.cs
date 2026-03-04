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
    /// Optional delegate invoked on the UI thread when a wallpaper is about to change.
    /// Receives (oldImagePath, monitorBounds, durationMs, fitMode).
    /// Must block until the overlay window's first frame is composited, then return an
    /// Action that starts the fade animation.  The engine calls the returned Action AFTER
    /// SetWallpaper so the reveal always exposes the new image.
    /// Wired up in App.xaml.cs so the engine stays decoupled from WPF windows.
    /// </summary>
    public Func<string, System.Windows.Rect, int, Models.FitMode, Action>? ShowTransitionOverlay { get; set; }

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

        _libraryService.LibraryChanged += OnLibraryChanged;
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => RefreshImagePool();

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
        // Capture everything we need inside the lock, then do all UI/COM work outside.
        // Calling Dispatcher.Invoke *inside* the lock risks deadlock: if the UI thread is
        // already waiting to acquire this same lock (e.g. from a Skip command), the timer
        // thread blocks on Dispatcher.Invoke while the UI thread blocks on the lock.
        ImageEntry? next = null;
        string? oldImagePath = null;
        bool shouldTransition = false;
        System.Windows.Rect transitionBounds = default;
        int transitionDuration = 0;
        Models.FitMode transitionFitMode = Models.FitMode.Fill;

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

            next = state.ShuffleDeck[state.DeckIndex++];

            if (string.IsNullOrEmpty(state.Monitor.WallpaperDevicePath))
            {
                AppLogger.Error($"Monitor {state.Monitor.DeviceId} has no wallpaper device path — skipping wallpaper set");
                return;
            }

            // Capture transition parameters while holding the lock; execute outside it.
            oldImagePath = state.SlideshowState.CurrentImage?.FilePath;
            if (_appSettings.TransitionsEnabled
                && !string.IsNullOrEmpty(oldImagePath)
                && !state.Monitor.Bounds.IsEmpty
                && ShowTransitionOverlay is not null)
            {
                shouldTransition   = true;
                transitionBounds   = state.Monitor.Bounds;
                transitionDuration = _appSettings.TransitionDurationMs;
                transitionFitMode  = state.Config.FitMode;
            }
        }

        // Correct transition sequence (outside the lock — no deadlock risk):
        //
        //  1. Invoke (synchronous) → ShowTransitionOverlay creates the overlay window,
        //     then BLOCKS via Dispatcher.PushFrame until the first frame is composited.
        //     This guarantees the overlay is visible before we touch the wallpaper.
        //
        //  2. SetWallpaper — safe to call now because the overlay covers the desktop.
        //
        //  3. Invoke → BeginFade() starts the opacity animation, revealing the new image.
        //
        // Using Invoke (not InvokeAsync) is safe here because the lock is already released.
        // If the UI thread is blocked on AdvanceLock (e.g. during a Skip), the lock is
        // released before we reach this point, so no deadlock can occur.

        Action? beginFade = null;
        if (shouldTransition)
        {
            var oldPath  = oldImagePath!;
            var bounds   = transitionBounds;
            var duration = transitionDuration;
            var fitMode  = transitionFitMode;
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => beginFade = ShowTransitionOverlay!(oldPath, bounds, duration, fitMode));
        }

        try
        {
            _wallpaperService.SetWallpaper(state.Monitor.WallpaperDevicePath!, next!.FilePath, state.Config.FitMode);
        }
        catch (Exception ex) { AppLogger.Error($"SetWallpaper failed for monitor {state.Monitor.DeviceId}", ex); return; }

        // Now that the new wallpaper is in place, start the fade to reveal it.
        if (beginFade is not null)
            System.Windows.Application.Current.Dispatcher.Invoke(beginFade);

        state.SlideshowState.CurrentImage = next;
        state.SlideshowState.Status = SlideshowStatus.Playing;
        state.SlideshowState.NextChangeAt = DateTime.UtcNow.AddSeconds(state.Config.IntervalSeconds);

        if (!suppressStateChanged)
            RaiseStateChanged(state.SlideshowState);
    }

    private volatile IReadOnlyList<Models.ImageEntry> _cachedImages = Array.Empty<Models.ImageEntry>();
    private CancellationTokenSource? _poolRefreshCts;
    private volatile int _poolVersion = 0;

    private void RefreshImagePool()
    {
        _poolRefreshCts?.Cancel();
        _poolRefreshCts?.Dispose();
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
        _libraryService.LibraryChanged -= OnLibraryChanged;
        _poolRefreshCts?.Cancel();
        _poolRefreshCts?.Dispose();
        _poolRefreshCts = null;
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
