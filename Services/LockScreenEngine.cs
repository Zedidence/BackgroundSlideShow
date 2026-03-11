using System.IO;
using System.Runtime.InteropServices;

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

    private readonly LockScreenService _lockScreenService;
    private readonly AppSettings       _appSettings;
    private readonly Random            _rng = new();

    private List<string>             _deck    = new();
    private int                      _deckPos = 0;
    private int                      _collageCountdown = 0; // decrements per transition; collage fires at 0
    private System.Timers.Timer?     _timer;
    private readonly object          _lock = new();

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
        StopTimer();

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
            _deck             = Shuffle(files);
            _deckPos          = 0;
            _collageCountdown = NextCollageCountdown();
            IsRunning         = true;
        }

        // Apply the first image immediately (always a single on first start), then begin timer.
        await ApplyCurrentAsync();
        StartTimer();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        StopTimer();
        lock (_lock)
        {
            _deck.Clear();
            IsRunning = false;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task NextAsync()
    {
        lock (_lock)
        {
            if (!IsRunning || _deck.Count <= 1) return;
            _deckPos = (_deckPos + 1) % _deck.Count;
        }
        await ApplyCurrentAsync();
        RestartTimer();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task ApplyCurrentAsync()
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

        if (shouldCollage)
        {
            var collagePath = await BuildCollageAsync();
            if (collagePath != null)
            {
                await _lockScreenService.SetLockScreenImageAsync(collagePath);
                return;
            }
            // Fall through to single image if collage composition failed.
        }

        await _lockScreenService.SetLockScreenImageAsync(singlePath);
    }

    /// <summary>
    /// Composites a collage on a thread-pool thread and returns the path to
    /// the temporary JPEG, or <c>null</c> if composition fails.
    /// </summary>
    private Task<string?> BuildCollageAsync()
    {
        List<string>  images;
        CollageLayout layout;

        lock (_lock)
        {
            if (_deck.Count < 2) return Task.FromResult<string?>(null);

            layout = CollageComposer.PickLayout(_deck.Count, _rng);
            int needed = CollageComposer.ImagesNeeded(layout);

            images = Enumerable.Range(0, needed)
                .Select(i => _deck[(_deckPos + i) % _deck.Count])
                .ToList();
        }

        return Task.Run<string?>(() =>
        {
            try
            {
                var (w, h) = GetScreenSize();
                if (w <= 0 || h <= 0) { w = 1920; h = 1080; }

                Directory.CreateDirectory(Path.GetDirectoryName(CollageTempPath)!);
                CollageComposer.Compose(layout, images, w, h, CollageTempPath);
                AppLogger.Info($"Collage composed — layout={layout}, {images.Count} images → {w}×{h}");
                return CollageTempPath;
            }
            catch (Exception ex)
            {
                AppLogger.Error("CollageComposer.Compose failed", ex);
                return null;
            }
        });
    }

    private async void OnTimerElapsed(object? source, System.Timers.ElapsedEventArgs e)
    {
        lock (_lock)
        {
            if (!IsRunning || _deck.Count == 0) return;
            _deckPos = (_deckPos + 1) % _deck.Count;
        }
        await ApplyCurrentAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartTimer()
    {
        var ms = Math.Max(1, _appSettings.LockScreenIntervalMinutes) * 60_000.0;
        _timer = new System.Timers.Timer(ms) { AutoReset = true };
        _timer.Elapsed += OnTimerElapsed;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Elapsed -= OnTimerElapsed;
        _timer.Dispose();
        _timer = null;
    }

    private void RestartTimer()
    {
        StopTimer();
        if (IsRunning) StartTimer();
    }

    /// <summary>Returns a random countdown interval matching Windows' cadence (4–8 singles).</summary>
    private int NextCollageCountdown() => _rng.Next(4, 9);

    private static List<string> Shuffle(List<string> source)
    {
        var list = new List<string>(source);
        var rng  = new Random();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
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

    public void Dispose() => Stop();
}
