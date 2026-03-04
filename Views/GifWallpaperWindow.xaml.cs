using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BackgroundSlideShow.Views;

/// <summary>
/// Full-screen borderless window that sits at HWND_BOTTOM and animates a GIF
/// frame-by-frame using per-frame delay metadata from the GIF file.
///
/// Lifecycle:
///   1. Caller creates the window, calls Show(), then calls LoadGif(path).
///   2. LoadGif decodes all frames via GifBitmapDecoder and starts the frame timer.
///   3. Stop() halts the timer and releases frame references.
///   4. LoadGif() can be called again at any time to switch to a different GIF.
/// </summary>
public partial class GifWallpaperWindow : Window
{
    // ── Win32: position window in physical pixels regardless of DPI ───────────

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_BOTTOM = new(1);

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOSIZE     = 0x0001;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly Rect _monitorBounds;
    private List<(BitmapFrame Frame, int DelayMs)> _frames = new();
    private int _frameIndex;
    private DispatcherTimer? _frameTimer;

    public GifWallpaperWindow(Rect monitorBounds)
    {
        InitializeComponent();
        _monitorBounds = monitorBounds;

        // Initial WPF position — corrected to physical pixels in OnSourceInitialized.
        Left   = monitorBounds.Left;
        Top    = monitorBounds.Top;
        Width  = monitorBounds.Width;
        Height = monitorBounds.Height;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_BOTTOM,
            (int)_monitorBounds.Left, (int)_monitorBounds.Top,
            (int)_monitorBounds.Width, (int)_monitorBounds.Height,
            SWP_NOACTIVATE);
    }

    /// <summary>
    /// Re-order to HWND_BOTTOM any time WPF tries to activate (raise) the window.
    /// </summary>
    protected override void OnActivated(EventArgs e) => SendToBottom();

    /// <summary>Pushes the window behind all normal app windows (above wallpaper only).</summary>
    internal void SendToBottom()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
    }

    // ── GIF playback ──────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes all frames from <paramref name="path"/> and begins animating them.
    /// Safe to call while a GIF is already playing — stops the previous animation first.
    /// </summary>
    public void LoadGif(string path)
    {
        // Stop any in-progress animation and release previous frame references.
        StopFrameTimer();
        _frames.Clear();
        _frameIndex = 0;
        GifFrame.Source = null;

        try
        {
            var decoder = new GifBitmapDecoder(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            foreach (var frame in decoder.Frames)
            {
                int delayMs = 100; // fallback: 10 fps
                // GIF frame delay is stored as 1/100 s in the Graphic Control Extension.
                // Use 'as' cast so a null Metadata object yields null rather than NullReferenceException.
                if (frame.Metadata is BitmapMetadata meta &&
                    meta.GetQuery("/grctlext/Delay") is ushort d)
                {
                    delayMs = Math.Max(20, d * 10); // clamp to ≥ 20 ms (50 fps max)
                }

                _frames.Add((frame, delayMs));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"GifWallpaperWindow: failed to load '{path}'", ex);
            return;
        }

        if (_frames.Count == 0) return;

        GifFrame.Source = _frames[0].Frame;

        // Single-frame "GIF" is effectively a static image — no timer needed.
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
        StopFrameTimer();
        _frames.Clear();
        GifFrame.Source = null;
    }
}
