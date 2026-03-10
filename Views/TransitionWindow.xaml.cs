using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BackgroundSlideShow;
using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.Views;

/// <summary>
/// Full-screen borderless overlay that crossfades between the old and new wallpaper.
///
/// Lifecycle:
///   1. Caller creates the window and calls Show().
///   2. OnContentRendered fires (first WPF frame committed to DWM).
///   3. Caller calls SetWallpaper externally.
///   4. Caller calls BeginFade() — opacity animates 1→0, revealing the new wallpaper.
///   5. Window self-closes when the animation completes.
///
/// This ordering guarantees the overlay is always visible before the wallpaper
/// changes, and the fade only starts after the new wallpaper is ready underneath.
/// </summary>
public partial class TransitionWindow : Window
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
    private readonly int  _durationMs;
    private bool _fadeStarted;
    private DispatcherTimer? _fallbackTimer;

    public bool IsImageLoaded { get; private set; }

    public TransitionWindow(string oldImagePath, Rect monitorBounds, int durationMs, FitMode fitMode)
    {
        InitializeComponent();

        _monitorBounds = monitorBounds;
        _durationMs    = durationMs;

        // Initial position — will be corrected to physical pixels in OnSourceInitialized.
        Left   = monitorBounds.Left;
        Top    = monitorBounds.Top;
        Width  = monitorBounds.Width;
        Height = monitorBounds.Height;

        // Match the overlay's stretch mode to the wallpaper fit setting so the
        // image displayed during the transition looks identical to the old wallpaper.
        OldImage.Stretch = fitMode switch
        {
            FitMode.Fill    => Stretch.UniformToFill,
            FitMode.Fit     => Stretch.Uniform,
            FitMode.Stretch => Stretch.Fill,
            FitMode.Center  => Stretch.None,
            FitMode.Tile    => Stretch.UniformToFill, // WPF Image can't tile; best approximation
            _               => Stretch.UniformToFill,
        };

        if (fitMode == FitMode.Center)
        {
            OldImage.HorizontalAlignment = HorizontalAlignment.Center;
            OldImage.VerticalAlignment   = VerticalAlignment.Center;
        }

        // Load the old wallpaper image.  Prefer the thumbnail cache for speed — the
        // fade is brief enough that pixel-perfect resolution isn't necessary.
        // DecodePixelWidth is capped at 1920 so 8K+ source files don't cause WPF's
        // BitmapImage decoder to allocate hundreds of MB for a brief fade overlay.
        try
        {
            string loadPath = oldImagePath;
            if (Services.ThumbnailCacheService.TryGetCachedPath(oldImagePath, out var cachedPath))
                loadPath = cachedPath;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource        = new Uri(loadPath);
            bmp.DecodePixelWidth = Math.Min((int)monitorBounds.Width, 1920);
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            OldImage.Source = bmp;
            IsImageLoaded   = true;
        }
        catch (Exception ex)
        {
            // If the image can't be loaded just close immediately — better no transition
            // than an empty overlay blocking the desktop.
            AppLogger.Warn($"TransitionWindow: failed to load image '{oldImagePath}': {ex.Message}");
            IsImageLoaded = false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Set physical-pixel position before ShowWindow is called.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_BOTTOM,
            (int)_monitorBounds.Left, (int)_monitorBounds.Top,
            (int)_monitorBounds.Width, (int)_monitorBounds.Height,
            SWP_NOACTIVATE);
    }

    /// <summary>
    /// WPF's ShowWindow(SW_SHOWNOACTIVATE) internally re-orders the window to HWND_TOP
    /// even though we set HWND_BOTTOM in OnSourceInitialized.  Calling SendToBottom()
    /// right after Show() (and again here as a fallback) keeps us at desktop layer.
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        SendToBottom();
    }

    /// <summary>Pushes the window behind all normal app windows (above wallpaper only).</summary>
    internal void SendToBottom()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (!IsImageLoaded)
        {
            Close();
            return;
        }

        // Safety valve: if BeginFade() is never called (e.g., SetWallpaper errored),
        // auto-start the fade after a generous timeout so the window doesn't get stranded.
        _fallbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(_durationMs * 3, 2000))
        };
        _fallbackTimer.Tick += (_, _) => { _fallbackTimer.Stop(); BeginFade(); };
        _fallbackTimer.Start();
    }

    /// <summary>
    /// Starts the fade-out animation. Must be called on the UI thread AFTER the new
    /// wallpaper has been applied, so the reveal exposes the correct image.
    /// Safe to call multiple times — only the first call takes effect.
    /// </summary>
    public void BeginFade()
    {
        if (_fadeStarted || !IsLoaded || !IsVisible) return;
        _fadeStarted = true;

        // Stop the safety-valve timer now that we have a real fade to run.
        _fallbackTimer?.Stop();
        _fallbackTimer = null;

        var anim = new DoubleAnimation(1.0, 0.0,
            new Duration(TimeSpan.FromMilliseconds(_durationMs)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        anim.Completed += (_, _) =>
        {
            OldImage.Source = null; // release the bitmap immediately rather than waiting for GC
            Close();
        };
        OldImage.BeginAnimation(OpacityProperty, anim);
    }
}
