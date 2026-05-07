using System.IO;
using System.Runtime.InteropServices;
using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Rotates the Windows lock screen image on a configurable interval.
/// Images are picked from a folder in a shuffled order.
///
/// Periodically (every 4–8 single images, mimicking Windows) a photo collage
/// is composited and applied instead of a single image.
///
/// The timer runs on a thread-pool thread; <see cref="StateChanged"/> may
/// fire from any thread — callers that update UI must marshal accordingly.
/// </summary>
public sealed class LockScreenEngine : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".heic", ".heif" };

    private static readonly string CollageTempPath = Path.Combine(
        AppSettings.AppDataFolder, "lockscreen_collage.jpg");

    private static readonly string SingleTempPath = Path.Combine(
        AppSettings.AppDataFolder, "lockscreen_single.jpg");

    private readonly LockScreenService _lockScreenService;
    private readonly AppSettings       _appSettings;

    private List<string>             _deck    = new();
    private int                      _deckPos = 0;
    private int                      _collageCountdown = 0; // decrements per transition; collage fires at 0
    private System.Timers.Timer?     _timer;
    private readonly object          _lock = new();

    // Tracks the in-flight ApplyCurrentAsync so Stop()/Dispose() can wait for it
    // before tearing down. Without this, a tick mid-flight would still call
    // SetLockScreenImageAsync on a stopped engine.
    private Task? _inFlightApply;
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    public bool   IsRunning       { get; private set; }
    public int    ImageCount      => _deck.Count;
    public string CurrentFileName =>
        _deck.Count > 0 ? Path.GetFileName(_deck[_deckPos]) : "";

    public event EventHandler? StateChanged;

    public LockScreenEngine(LockScreenService lockScreenService, AppSettings appSettings)
    {
        _lockScreenService = lockScreenService;
        _appSettings       = appSettings;
    }

    // ── Public control ────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        await StopAsync();

        var folder = _appSettings.LockScreenFolderPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            IsRunning = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        if (files.Count == 0)
        {
            IsRunning = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        lock (_lock)
        {
            _cts              = new CancellationTokenSource();
            _deck             = Shuffle(files);
            _deckPos          = 0;
            _collageCountdown = NextCollageCountdown();
            IsRunning         = true;
        }

        // Apply the first image immediately (always a single on first start), then begin timer.
        await ApplyCurrentTrackedAsync();
        StartTimer();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the timer and waits for any in-flight wallpaper apply to finish.</summary>
    public async Task StopAsync()
    {
        Task? toAwait;
        lock (_lock)
        {
            StopTimerLocked();
            _cts.Cancel();
            toAwait = _inFlightApply;
            _deck.Clear();
            IsRunning = false;
        }

        if (toAwait is not null)
        {
            try { await toAwait; }
            catch { /* already logged in tracker */ }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Synchronous shim for callers that can't await (UI button handlers using fire-and-forget).</summary>
    public void Stop() => _ = StopAsync();

    public async Task NextAsync()
    {
        lock (_lock)
        {
            if (!IsRunning || _deck.Count <= 1) return;
            _deckPos = (_deckPos + 1) % _deck.Count;
        }
        await ApplyCurrentTrackedAsync();
        RestartTimer();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private Task ApplyCurrentTrackedAsync()
    {
        var token = _cts.Token;
        var task = ApplyCurrentAsync(token);
        lock (_lock) { _inFlightApply = task; }
        return task;
    }

    private async Task ApplyCurrentAsync(CancellationToken ct)
    {
        string singlePath   = "";
        bool   shouldCollage = false;

        lock (_lock)
        {
            if (_deck.Count == 0) return;
            singlePath = _deck[_deckPos];

            if (_appSettings.LockScreenCollageEnabled && _deck.Count >= 2)
            {
                if (--_collageCountdown <= 0)
                {
                    _collageCountdown = NextCollageCountdown();
                    shouldCollage     = true;
                }
            }
        }

        if (ct.IsCancellationRequested) return;

        if (shouldCollage)
        {
            var collagePath = await BuildCollageAsync(ct);
            if (ct.IsCancellationRequested) return;
            if (collagePath != null)
            {
                await _lockScreenService.SetLockScreenImageAsync(collagePath);
                return;
            }
            // Fall through to single image if collage composition failed.
        }

        await ApplySingleAsync(singlePath, ct);
    }

    /// <summary>
    /// Pre-composites a single image onto a screen-sized canvas using the
    /// user's chosen FitMode, then sets it as the lock screen.
    /// Falls back to the raw file if composition fails.
    /// </summary>
    private async Task ApplySingleAsync(string sourcePath, CancellationToken ct)
    {
        var fitMode = _appSettings.LockScreenFitMode;
        var processedPath = await Task.Run<string>(() =>
        {
            try
            {
                var (w, h) = GetScreenSize();
                if (w <= 0 || h <= 0) { w = 1920; h = 1080; }
                Directory.CreateDirectory(Path.GetDirectoryName(SingleTempPath)!);
                CollageComposer.ComposeSingle(sourcePath, fitMode, w, h, SingleTempPath);
                return SingleTempPath;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"ComposeSingle failed for '{sourcePath}' — {ex.Message}");
                return sourcePath;
            }
        }, ct);

        if (ct.IsCancellationRequested) return;
        await _lockScreenService.SetLockScreenImageAsync(processedPath);
    }

    /// <summary>
    /// Composites a collage on a thread-pool thread and returns the path to
    /// the temporary JPEG, or <c>null</c> if composition fails.
    /// </summary>
    private Task<string?> BuildCollageAsync(CancellationToken ct)
    {
        List<string> images;

        lock (_lock)
        {
            if (_deck.Count < 2) return Task.FromResult<string?>(null);

            int count = CollageComposer.PickImageCount(_deck.Count, Random.Shared);
            images = Enumerable.Range(0, count)
                .Select(i => _deck[(_deckPos + i) % _deck.Count])
                .ToList();
        }

        return Task.Run<string?>(() =>
        {
            try
            {
                var (w, h) = GetScreenSize();
                if (w <= 0 || h <= 0) { w = 1920; h = 1080; }

                var (bestLayout, orderedPaths) = CollageComposer.PickBestLayoutFromPaths(images, w, h, Random.Shared);

                Directory.CreateDirectory(Path.GetDirectoryName(CollageTempPath)!);
                CollageComposer.Compose(bestLayout, orderedPaths, w, h, CollageTempPath);
                AppLogger.Info($"Collage composed — layout={bestLayout}, {orderedPaths.Count} images → {w}×{h}");
                return CollageTempPath;
            }
            catch (Exception ex)
            {
                AppLogger.Error("CollageComposer.Compose failed", ex);
                return null;
            }
        }, ct);
    }

    // async void is necessary because System.Timers.Timer.Elapsed is a sync event,
    // but we wrap the whole body in try/catch so an exception never reaches
    // AppDomain.UnhandledException and the timer keeps ticking with sane state.
    private async void OnTimerElapsed(object? source, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            lock (_lock)
            {
                if (!IsRunning || _deck.Count == 0) return;
                _deckPos = (_deckPos + 1) % _deck.Count;
            }
            await ApplyCurrentTrackedAsync();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { /* engine stopping */ }
        catch (Exception ex) { AppLogger.Error("LockScreenEngine timer tick failed", ex); }
    }

    private void StartTimer()
    {
        lock (_lock)
        {
            StopTimerLocked();
            var ms = Math.Max(1, _appSettings.LockScreenIntervalMinutes) * 60_000.0;
            _timer = new System.Timers.Timer(ms) { AutoReset = true };
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
        }
    }

    private void StopTimerLocked()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Elapsed -= OnTimerElapsed;
        _timer.Dispose();
        _timer = null;
    }

    private void RestartTimer()
    {
        if (IsRunning) StartTimer();
    }

    /// <summary>Returns a random countdown interval matching Windows' cadence (4–8 singles).</summary>
    private static int NextCollageCountdown() => Random.Shared.Next(4, 9);

    private static List<string> Shuffle(List<string> source)
    {
        var list = new List<string>(source);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // SM_CXSCREEN = 0, SM_CYSCREEN = 1
    private static (int W, int H) GetScreenSize() =>
        (GetSystemMetrics(0), GetSystemMetrics(1));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Block briefly so any in-flight wallpaper apply finishes before AppData is unwound.
        try { StopAsync().GetAwaiter().GetResult(); }
        catch { /* shutting down */ }
        _cts.Dispose();
    }
}
