using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BackgroundSlideShow.Views;

/// <summary>
/// Full-screen borderless window that sits behind desktop icons (embedded in the
/// WorkerW layer) and animates a GIF frame-by-frame using per-frame delay metadata.
///
/// Lifecycle:
///   1. Caller creates the window, calls Show(), then calls LoadGifAsync(path).
///   2. LoadGifAsync decodes all frames via GifBitmapDecoder and starts the frame timer.
///   3. Stop() halts the timer and releases frame references.
///   4. LoadGifAsync() can be called again at any time to switch to a different GIF.
/// </summary>
public partial class GifWallpaperWindow : Window
{
    // ── Win32 ────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg,
        UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SMTO_NORMAL    = 0x0000;

    // ── Fields ──────────────────────────────────────────────────────────────

    private readonly Rect _monitorBounds; // physical pixels from GDI
    private List<(BitmapFrame Frame, int DelayMs)> _frames = new();
    private int _frameIndex;
    private DispatcherTimer? _frameTimer;
    private CancellationTokenSource? _loadCts;

    public GifWallpaperWindow(Rect monitorBounds)
    {
        InitializeComponent();
        _monitorBounds = monitorBounds;

        // Set initial WPF position — we'll correct with SetWindowPos in OnSourceInitialized.
        // Use physical pixel values directly; DPI correction happens via SetWindowPos.
        Left   = monitorBounds.Left;
        Top    = monitorBounds.Top;
        Width  = monitorBounds.Width;
        Height = monitorBounds.Height;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // Position with physical pixel coordinates via SetWindowPos (bypasses WPF DPI scaling).
        SetWindowPos(hwnd, HWND_BOTTOM,
            (int)_monitorBounds.Left, (int)_monitorBounds.Top,
            (int)_monitorBounds.Width, (int)_monitorBounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // Attempt to embed behind desktop icons via WorkerW.
        EmbedBehindIcons(hwnd);
    }

    /// <summary>
    /// Finds or creates the WorkerW window behind desktop icons and parents this
    /// window into it, so the GIF appears below icons but above the static wallpaper.
    /// Falls back to HWND_BOTTOM if the WorkerW trick fails.
    /// </summary>
    private void EmbedBehindIcons(IntPtr hwnd)
    {
        try
        {
            var progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return;

            // Ask Progman to spawn a WorkerW behind the desktop icons.
            SendMessageTimeout(progman, 0x052C, UIntPtr.Zero, IntPtr.Zero,
                SMTO_NORMAL, 1000, out _);

            // Find the WorkerW that sits between Progman and the desktop icon layer.
            IntPtr workerW = IntPtr.Zero;
            IntPtr child = IntPtr.Zero;
            while (true)
            {
                child = FindWindowEx(IntPtr.Zero, child, "WorkerW", null);
                if (child == IntPtr.Zero) break;

                var shellView = FindWindowEx(child, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                {
                    // The *next* WorkerW after the one containing SHELLDLL_DefView is our target.
                    workerW = FindWindowEx(IntPtr.Zero, child, "WorkerW", null);
                    break;
                }
            }

            if (workerW != IntPtr.Zero)
            {
                SetParent(hwnd, workerW);
                // Re-apply position relative to the WorkerW parent (which spans virtual desktop).
                SetWindowPos(hwnd, IntPtr.Zero,
                    (int)_monitorBounds.Left, (int)_monitorBounds.Top,
                    (int)_monitorBounds.Width, (int)_monitorBounds.Height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"GifWallpaperWindow: WorkerW embedding failed, using HWND_BOTTOM fallback. {ex.Message}");
        }
    }

    /// <summary>
    /// Re-order to HWND_BOTTOM any time WPF tries to activate (raise) the window.
    /// Only needed when not embedded in WorkerW.
    /// </summary>
    protected override void OnActivated(EventArgs e) => SendToBottom();

    internal void SendToBottom()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
    }

    // ── GIF playback ────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes all frames from <paramref name="path"/> on a background thread and begins animating.
    /// Safe to call while a GIF is already playing — cancels the previous load and stops animation first.
    /// </summary>
    public async Task LoadGifAsync(string path)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        StopFrameTimer();

        // Release old frames immediately to reduce peak memory.
        _frames.Clear();
        _frameIndex = 0;
        GifFrame.Source = null;

        List<(BitmapFrame Frame, int DelayMs)> frames;
        try
        {
            frames = await Task.Run(() =>
            {
                var decoder = new GifBitmapDecoder(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                var result = new List<(BitmapFrame Frame, int DelayMs)>(decoder.Frames.Count);
                foreach (var frame in decoder.Frames)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    int delayMs = 100; // fallback: 10 fps
                    if (frame.Metadata is BitmapMetadata meta)
                    {
                        try
                        {
                            var delayObj = meta.GetQuery("/grctlext/Delay");
                            delayMs = delayObj switch
                            {
                                ushort d => Math.Max(20, d * 10),
                                int d    => Math.Max(20, d * 10),
                                short d  => Math.Max(20, d * 10),
                                _        => 100
                            };
                        }
                        catch
                        {
                            // Metadata query failed — use fallback.
                        }
                    }

                    if (frame.CanFreeze) frame.Freeze();
                    result.Add((frame, delayMs));
                }
                return result;
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"GifWallpaperWindow: failed to load '{path}'", ex);
            return;
        }

        if (cts.IsCancellationRequested) return;

        _frames = frames;
        if (_frames.Count == 0) return;

        GifFrame.Source = _frames[0].Frame;

        if (_frames.Count == 1) return;

        var ft = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_frames[0].DelayMs) };
        ft.Tick += AdvanceFrame;
        ft.Start();
        _frameTimer = ft;
    }

    private void AdvanceFrame(object? s, EventArgs e)
    {
        if (_frames.Count == 0) return;

        _frameTimer!.Stop();
        _frameIndex = (_frameIndex + 1) % _frames.Count;
        GifFrame.Source = _frames[_frameIndex].Frame;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(_frames[_frameIndex].DelayMs);
        _frameTimer.Start();
    }

    private void StopFrameTimer()
    {
        if (_frameTimer is null) return;
        _frameTimer.Stop();
        _frameTimer.Tick -= AdvanceFrame;
        _frameTimer = null;
    }

    /// <summary>Stops animation and releases all frame references.</summary>
    public void Stop()
    {
        _loadCts?.Cancel();
        StopFrameTimer();
        _frames.Clear();
        GifFrame.Source = null;
    }
}
