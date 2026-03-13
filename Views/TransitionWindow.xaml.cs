using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BackgroundSlideShow;
using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.Views;

/// <summary>
/// Full-screen borderless overlay that crossfades between two wallpapers.
///
/// Design notes:
/// • AllowsTransparency=False — NewImage always covers the full window area so the
///   window is never visually transparent.  Avoiding WS_EX_LAYERED eliminates the
///   DWM full-recomposition pass (and resulting taskbar flash) that layered windows
///   trigger on create/destroy.
/// • Z-order: placed directly below the desktop icon layer (the top-level window
///   containing SHELLDLL_DefView).  DesktopInterop sends the Progman 0x052C message
///   on first use to ensure the desktop is in the transparent-composited hierarchy,
///   so our window is visible through the DWM background.  Icons, taskbar, and all
///   app windows remain above us.  Falls back to HWND_BOTTOM if the icon layer
///   can't be found.
/// • WM_WINDOWPOSCHANGING hook: WPF calls its own SetWindowPos during Show() to
///   apply Left/Top/Width/Height, which would silently reset z-order, position, and
///   size.  We intercept that message and inject SWP_NOZORDER | SWP_NOMOVE |
///   SWP_NOSIZE so our placement is permanent.
/// • EnsureHandle() must be called by the caller BEFORE Show() so OnSourceInitialized
///   fires (setting WS_EX_TOOLWINDOW + z-order) before the window is ever visible.
///
/// Lifecycle:
///   1. Caller calls EnsureHandle() → OnSourceInitialized runs (sets styles + z-order).
///   2. Caller calls Show() → window appears at correct z-position.
///   3. ContentRendered fires (first WPF frame composited to DWM).
///   4. Caller calls SetWallpaper externally.
///   5. Caller calls BeginFade() → OldImage opacity 1→0, revealing NewImage.
///   6. Window hides itself and self-closes when the animation completes.
/// </summary>
public partial class TransitionWindow : Window
{
    // ── Win32 ─────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int  GWL_EXSTYLE    = -20;
    private const int  WS_EX_NOACTIVATE     = 0x08000000;
    private const int  WS_EX_TOOLWINDOW     = 0x00000080;
    private const int  WM_WINDOWPOSCHANGING = 0x0046;

    // HWND_BOTTOM (1): fallback z-order position when the desktop icon layer can't be
    // found.  The preferred path uses DesktopInterop.FindDesktopIconLayer() to place us
    // directly below the icon layer for reliable visibility on all Win10/11 builds.
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly Rect _monitorBounds;
    private readonly int  _durationMs;
    private bool _positionLocked;
    private bool _fadeStarted;
    private DispatcherTimer? _fallbackTimer;

    public bool IsImageLoaded { get; private set; }

    public TransitionWindow(string oldImagePath, string newImagePath,
                            Rect monitorBounds, int durationMs, FitMode fitMode)
    {
        InitializeComponent();

        _monitorBounds = monitorBounds;
        _durationMs    = durationMs;

        Left   = monitorBounds.Left;
        Top    = monitorBounds.Top;
        Width  = monitorBounds.Width;
        Height = monitorBounds.Height;

        var stretch = fitMode switch
        {
            FitMode.Fill    => Stretch.UniformToFill,
            FitMode.Fit     => Stretch.Uniform,
            FitMode.Stretch => Stretch.Fill,
            FitMode.Center  => Stretch.None,
            FitMode.Tile    => Stretch.UniformToFill,
            _               => Stretch.UniformToFill,
        };
        OldImage.Stretch = stretch;
        NewImage.Stretch = stretch;

        if (fitMode == FitMode.Center)
        {
            OldImage.HorizontalAlignment = HorizontalAlignment.Center;
            OldImage.VerticalAlignment   = VerticalAlignment.Center;
            NewImage.HorizontalAlignment = HorizontalAlignment.Center;
            NewImage.VerticalAlignment   = VerticalAlignment.Center;
        }

        int decodeWidth = Math.Min((int)monitorBounds.Width, 1920);

        // OldImage: skip thumbnail cache — thumbnails are small JPEGs that look blurry
        // at monitor scale, causing a quality discontinuity at the start of the fade.
        IsImageLoaded = TryLoadImage(oldImagePath, decodeWidth, skipCache: true, out var oldBmp);
        if (IsImageLoaded)
            OldImage.Source = oldBmp;

        // NewImage failure is non-fatal — fade still completes and wallpaper is set.
        if (TryLoadImage(newImagePath, decodeWidth, skipCache: false, out var newBmp))
            NewImage.Source = newBmp;

        // If the window is closed externally (e.g., stale-window replacement in
        // App.xaml.cs) before BeginFade() is called, stop the fallback timer so it
        // doesn't hold the window live in the dispatcher for up to durationMs*3 ms.
        Closed += (_, _) =>
        {
            _fallbackTimer?.Stop();
            _fallbackTimer = null;
        };
    }

    private static bool TryLoadImage(string path, int decodeWidth, bool skipCache,
                                     out BitmapImage? bmp)
    {
        bmp = null;
        try
        {
            string loadPath = path;
            if (!skipCache && Services.ThumbnailCacheService.TryGetCachedPath(path, out var cached))
                loadPath = cached;

            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource        = new Uri(loadPath);
            b.DecodePixelWidth = decodeWidth;
            b.CacheOption      = BitmapCacheOption.OnLoad;
            b.EndInit();
            b.Freeze();
            bmp = b;
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"TransitionWindow: failed to load '{path}': {ex.Message}");
            return false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        // Hook WndProc before anything else so no z-order message can slip through.
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        // WS_EX_NOACTIVATE: window can never be activated, focused window keeps its
        //   active title-bar colour throughout the transition.
        // WS_EX_TOOLWINDOW: hides from taskbar and Alt+Tab (belt-and-suspenders on top
        //   of ShowInTaskbar="False"; set here rather than in XAML so it is applied
        //   before ShowWindow ever registers the window with the shell).
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // Place directly below the desktop icon layer (the top-level window holding
        // SHELLDLL_DefView).  DesktopInterop.EnsureInitialized sends the Progman 0x052C
        // message on first call so the desktop is in the transparent-composited state
        // where our window is visible through DWM.  Falls back to HWND_BOTTOM if the
        // icon layer can't be found (e.g. custom shell replacements).
        var iconLayer = Services.DesktopInterop.FindDesktopIconLayer();
        var insertAfter = iconLayer != IntPtr.Zero ? iconLayer : HWND_BOTTOM;
        SetWindowPos(hwnd, insertAfter,
            (int)_monitorBounds.Left, (int)_monitorBounds.Top,
            (int)_monitorBounds.Width, (int)_monitorBounds.Height,
            SWP_NOACTIVATE);

        // Lock everything: WPF calls SetWindowPos during Show() to apply its own
        // Left/Top/Width/Height (DPI-scaled), which would silently reset z-order,
        // position, and size.  The hook injects SWP_NOZORDER | SWP_NOMOVE | SWP_NOSIZE
        // into every subsequent WM_WINDOWPOSCHANGING so our placement is permanent.
        _positionLocked = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && _positionLocked)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            wp.flags |= SWP_NOZORDER | SWP_NOMOVE | SWP_NOSIZE;
            Marshal.StructureToPtr(wp, lParam, false);
        }
        return IntPtr.Zero;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (!IsImageLoaded)
        {
            Close();
            return;
        }

        // Safety valve: auto-start the fade if BeginFade() is never called (e.g.,
        // SetWallpaper errored) so the window doesn't get stranded on the desktop.
        // Bug 6 fix: _durationMs * 3 overflows int for extreme values (> ~715 million ms),
        // producing a negative argument to TimeSpan.FromMilliseconds which throws OverflowException,
        // leaving the window stranded on the desktop.  Widen to long before multiplying.
        _fallbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max((long)_durationMs * 3, 2000))
        };
        _fallbackTimer.Tick += (_, _) => { _fallbackTimer.Stop(); BeginFade(); };
        _fallbackTimer.Start();
    }

    /// <summary>
    /// Starts the crossfade: OldImage opacity 1→0, NewImage stays solid underneath.
    /// Safe to call multiple times — only the first call takes effect.
    /// </summary>
    public void BeginFade()
    {
        if (_fadeStarted || !IsLoaded || !IsVisible) return;
        _fadeStarted = true;

        _fallbackTimer?.Stop();
        _fallbackTimer = null;

        var anim = new DoubleAnimation(1.0, 0.0,
            new Duration(TimeSpan.FromMilliseconds(_durationMs)));
        anim.Completed += (_, _) =>
        {
            // Hide before closing to prevent a black-frame flash.  The new wallpaper
            // is already set underneath, so hiding reveals it instantly with no gap.
            _positionLocked = false;
            Hide();
            OldImage.Source = null;
            NewImage.Source = null;
            Close();
        };
        OldImage.BeginAnimation(OpacityProperty, anim);
    }
}
