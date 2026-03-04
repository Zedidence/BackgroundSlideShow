using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Manages per-monitor animated-GIF wallpaper windows and handles cycling between
/// GIF files in a folder.
///
/// All public methods MUST be called on the UI thread (they create/close WPF windows).
///
/// Lifecycle:
///   Start()  → creates one GifWallpaperWindow per monitor, begins playing the first GIF,
///              starts the cycle timer.
///   Stop()   → closes all windows, clears state.
///   Next()   → immediately advance to the next GIF and restart the cycle timer.
///   Prev()   → immediately go back one GIF and restart the cycle timer.
/// </summary>
public sealed class GifPlayerEngine : IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly AppSettings    _appSettings;

    // One window per monitor device path.
    private readonly Dictionary<string, Views.GifWallpaperWindow> _windows = new();

    private List<string> _gifFiles  = new();
    private int          _gifIndex  = 0;
    private DispatcherTimer? _cycleTimer;

    public bool   IsRunning       => _windows.Count > 0;
    public int    GifCount        => _gifFiles.Count;
    public int    CurrentIndex    => _gifIndex;
    public string CurrentFileName =>
        _gifFiles.Count > 0 ? Path.GetFileName(_gifFiles[_gifIndex]) : "";

    public event EventHandler? StateChanged;

    public GifPlayerEngine(MonitorService monitorService, AppSettings appSettings)
    {
        _monitorService = monitorService;
        _appSettings    = appSettings;
    }

    // ── Public control ────────────────────────────────────────────────────────

    private static void AssertUiThread()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
            throw new InvalidOperationException(
                $"{nameof(GifPlayerEngine)} methods must be called on the UI thread.");
    }

    public void Start()
    {
        AssertUiThread();
        Stop(); // ensure clean slate

        var folder = _appSettings.GifFolderPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        _gifFiles = Directory.GetFiles(folder, "*.gif", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_gifFiles.Count == 0) return;

        _gifIndex = 0;
        var firstGif = _gifFiles[0];

        // Create one overlay window per connected monitor.
        foreach (var monitor in _monitorService.GetMonitors())
        {
            if (monitor.Bounds.IsEmpty) continue;

            var win = new Views.GifWallpaperWindow(monitor.Bounds);
            _windows[monitor.DeviceId] = win;
            win.Show();
            win.SendToBottom();
            win.LoadGif(firstGif);
        }

        if (_windows.Count == 0)
        {
            _gifFiles.Clear();
            return;
        }

        StartCycleTimer();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        AssertUiThread();
        StopCycleTimer();

        foreach (var win in _windows.Values)
        {
            win.Stop();
            win.Close();
        }
        _windows.Clear();
        _gifFiles.Clear();

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Next()
    {
        AssertUiThread();
        if (!IsRunning || _gifFiles.Count <= 1) return;
        AdvanceTo((_gifIndex + 1) % _gifFiles.Count);
    }

    public void Prev()
    {
        AssertUiThread();
        if (!IsRunning || _gifFiles.Count <= 1) return;
        AdvanceTo((_gifIndex - 1 + _gifFiles.Count) % _gifFiles.Count);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void AdvanceTo(int index)
    {
        _gifIndex = index;
        var path = _gifFiles[_gifIndex];

        foreach (var win in _windows.Values)
            win.LoadGif(path);

        // Restart so the new GIF always gets its full configured duration.
        RestartCycleTimer();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartCycleTimer()
    {
        var seconds = Math.Max(1, _appSettings.GifSecondsPerFile);
        _cycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _cycleTimer.Tick += OnCycleTick;
        _cycleTimer.Start();
    }

    private void StopCycleTimer()
    {
        if (_cycleTimer is null) return;
        _cycleTimer.Stop();
        _cycleTimer.Tick -= OnCycleTick;
        _cycleTimer = null;
    }

    private void RestartCycleTimer()
    {
        StopCycleTimer();
        StartCycleTimer();
    }

    private void OnCycleTick(object? s, EventArgs e) =>
        AdvanceTo((_gifIndex + 1) % _gifFiles.Count);

    public void Dispose() => Stop();
}
