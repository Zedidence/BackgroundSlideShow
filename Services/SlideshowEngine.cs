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
    private readonly Random _rng = new();

    public event EventHandler<SlideshowStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Optional delegate invoked on the UI thread when a wallpaper is about to change.
    /// Receives (oldImagePath, newImagePath, monitorBounds, durationMs, fitMode).
    /// Must block until the overlay window's first frame is composited, then return an
    /// Action that starts the crossfade animation.  The engine calls the returned Action
    /// AFTER SetWallpaper so the actual wallpaper is consistent with the overlay's NewImage
    /// by the time the fade completes.
    /// Wired up in App.xaml.cs so the engine stays decoupled from WPF windows.
    /// </summary>
    public Func<string, string, System.Windows.Rect, int, Models.FitMode, Action>? ShowTransitionOverlay { get; set; }

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
        if (!_states.TryGetValue(config.MonitorId, out var state) || state.Timer is null) return;

        // Bug 2 fix: setting Interval resets the internal timer countdown, but NextChangeAt
        // is never updated here — the countdown display shows stale remaining time until the
        // next tick fires AdvanceMonitor.  Update it now so the UI is immediately correct.
        state.Timer.Interval = config.IntervalSeconds * 1000.0;
        state.SlideshowState.NextChangeAt = DateTime.UtcNow.AddSeconds(config.IntervalSeconds);
        RaiseStateChanged(state.SlideshowState);
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
        List<string>? collageImages = null;
        CollageLayout collageLayout = default;

        lock (state.AdvanceLock)
        {
            var images = _cachedImages;
            if (images.Count == 0) return;

            // Rebuild the shuffle deck when the image pool has changed or the deck is exhausted.
            if (state.PoolVersion != _poolVersion || state.DeckIndex >= state.ShuffleDeck.Count)
            {
                // Capture recently-shown images before discarding the old deck so they
                // can be deferred to the back of the new deck, guaranteeing a fresh run
                // before any near-repeats can occur at the deck boundary.
                var recentPaths = CaptureRecentPaths(state);

                state.ShuffleDeck = _imageSelector.BuildShuffledDeck(
                    images, state.Monitor, state.Config, state.AllowedFolderIds);
                state.DeckIndex = 0;
                state.PoolVersion = _poolVersion;

                // Push recently-seen images to the back half of the new deck.
                // The front portion is guaranteed to be images not recently shown.
                if (recentPaths.Count > 0 && state.ShuffleDeck.Count > recentPaths.Count)
                {
                    var front = state.ShuffleDeck.Where(e => !recentPaths.Contains(e.FilePath)).ToList();
                    var back  = state.ShuffleDeck.Where(e =>  recentPaths.Contains(e.FilePath)).ToList();
                    state.ShuffleDeck = [..front, ..back];
                }
            }

            if (state.ShuffleDeck.Count == 0) return;

            // ShuffleDeck was built by BuildShuffledDeck → FilterWithFallback, so all images
            // in the deck (and therefore all collage panels) already respect the monitor's
            // smart orientation / aspect-ratio filter — no separate filtering needed here.
            //
            // Never produce two collages back-to-back: if the previous slide was a collage,
            // force a single image this time regardless of the chance roll.
            bool doCollage = state.Config.CollageEnabled
                && state.ShuffleDeck.Count >= 3
                && !state.LastWasCollage
                && _rng.Next(100) < state.Config.CollageChance;

            state.LastWasCollage = doCollage;

            if (doCollage)
            {
                collageLayout = CollageComposer.PickLayout(state.ShuffleDeck.Count, _rng);
                int needed = CollageComposer.ImagesNeeded(collageLayout);
                collageImages = Enumerable.Range(0, needed)
                    .Select(i => state.ShuffleDeck[(state.DeckIndex + i) % state.ShuffleDeck.Count].FilePath)
                    .ToList();
                next = state.ShuffleDeck[state.DeckIndex];
                // Advance by needed; cap at deck size so CaptureRecentPaths stays in bounds.
                state.DeckIndex = Math.Min(state.DeckIndex + needed, state.ShuffleDeck.Count);
            }
            else
            {
                next = state.ShuffleDeck[state.DeckIndex++];
            }

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
        //  1. Compose collage if needed — we need the final wallpaper path before showing
        //     the overlay so both old and new images can be loaded into it upfront.
        //
        //  2. Invoke (synchronous) → ShowTransitionOverlay creates the overlay window with
        //     both images loaded, then BLOCKS via Dispatcher.PushFrame until the first frame
        //     is composited.  The overlay is visible before we touch the actual wallpaper.
        //
        //  3. SetWallpaper — safe to call now because the overlay covers the desktop.
        //
        //  4. Invoke → BeginFade() starts the crossfade animation (OldImage opacity 1→0,
        //     NewImage already at full opacity below), revealing the new wallpaper.
        //
        // Using Invoke (not InvokeAsync) is safe here because the lock is already released.

        // Step 1 — resolve the final wallpaper path (compose collage if needed).
        string wallpaperPath = next!.FilePath;
        if (collageImages is not null)
        {
            try
            {
                int canvasW = (int)state.Monitor.Bounds.Width;
                int canvasH = (int)state.Monitor.Bounds.Height;
                if (canvasW <= 0 || canvasH <= 0) { canvasW = 1920; canvasH = 1080; }
                var tempPath = GetCollageTempPath(state.Monitor.DeviceId);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tempPath)!);
                CollageComposer.Compose(collageLayout, collageImages, canvasW, canvasH, tempPath);
                wallpaperPath = tempPath;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Wallpaper collage composition failed for {state.Monitor.DeviceId}", ex);
                // Fall through — wallpaperPath stays as the single image.
            }
        }

        // Step 2 — show the overlay (blocks until first frame composited).
        Action? beginFade = null;
        if (shouldTransition)
        {
            var oldPath    = oldImagePath!;
            var newPath    = wallpaperPath;
            var bounds     = transitionBounds;
            var duration   = transitionDuration;
            var fitMode    = transitionFitMode;
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => beginFade = ShowTransitionOverlay!(oldPath, newPath, bounds, duration, fitMode));
        }

        // Step 3 — set the new wallpaper (overlay is covering the desktop during this).
        try
        {
            _wallpaperService.SetWallpaper(state.Monitor.WallpaperDevicePath!, wallpaperPath, state.Config.FitMode);
        }
        catch (Exception ex) { AppLogger.Error($"SetWallpaper failed for monitor {state.Monitor.DeviceId}", ex); return; }

        // Step 4 — start the crossfade animation to reveal the new image.
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
        // Atomically replace the CTS so concurrent LibraryChanged events (which can fire
        // from background watcher threads) never double-dispose the same CTS object.
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _poolRefreshCts, cts);
        prev?.Cancel();
        prev?.Dispose();

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
        var cts = Interlocked.Exchange(ref _poolRefreshCts, null);
        cts?.Cancel();
        cts?.Dispose();
        foreach (var s in _states.Values)
        {
            s.Timer?.Stop();
            s.Timer?.Dispose();
        }
        _states.Clear();
    }

    /// <summary>
    /// Returns a stable per-monitor temp path for the composed collage JPEG.
    /// Uses a hash of the device ID to produce a short, safe filename.
    /// </summary>
    private static string GetCollageTempPath(string monitorDeviceId)
    {
        uint hash = (uint)monitorDeviceId.GetHashCode();
        return System.IO.Path.Combine(AppSettings.AppDataFolder, $"wallpaper_collage_{hash:X8}.jpg");
    }

    /// <summary>
    /// Returns the file paths of the most recently shown images from the current deck
    /// so they can be deferred to the back of the next shuffle pass.
    /// Captures at most half the deck size, capped at 20, to keep the "fresh" zone large.
    /// </summary>
    private static HashSet<string> CaptureRecentPaths(PerMonitorState state)
    {
        const int MaxTail = 20;
        int shown = state.DeckIndex; // number of images shown from the current deck
        if (shown == 0 || state.ShuffleDeck.Count == 0) return [];

        int tailCount = Math.Min(shown, Math.Min(state.ShuffleDeck.Count / 2, MaxTail));
        int from = shown - tailCount;
        return new HashSet<string>(
            state.ShuffleDeck.GetRange(from, tailCount).Select(e => e.FilePath),
            StringComparer.OrdinalIgnoreCase);
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
        /// <summary>True if the previous wallpaper advance produced a collage. Prevents back-to-back collages.</summary>
        public bool LastWasCollage { get; set; } = false;
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
