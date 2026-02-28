using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace BackgroundSlideShow.Views;

/// <summary>
/// Full-screen borderless overlay that crossfades between the old and new wallpaper.
/// The window covers a single monitor, displays the previous wallpaper image,
/// then animates its opacity from 1 → 0, revealing the new wallpaper behind it.
///
/// Lifecycle: created by the ShowTransitionOverlay delegate in App.xaml.cs,
/// self-closes when the animation completes.
/// </summary>
public partial class TransitionWindow : Window
{
    // ── Win32: position window in physical pixels regardless of DPI ───────────

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly Rect _monitorBounds;
    private readonly int  _durationMs;
    private bool _imageLoaded;

    public TransitionWindow(string oldImagePath, Rect monitorBounds, int durationMs)
    {
        InitializeComponent();

        _monitorBounds = monitorBounds;
        _durationMs    = durationMs;

        // Initial position — will be corrected to physical pixels in OnSourceInitialized.
        Left   = monitorBounds.Left;
        Top    = monitorBounds.Top;
        Width  = monitorBounds.Width;
        Height = monitorBounds.Height;

        // Load the old wallpaper image.  We decode at monitor width to keep memory reasonable
        // while still looking sharp (avoids decoding a 24 MP original at full resolution).
        try
        {
            // Prefer the thumbnail cache for speed — the fade is brief enough that
            // pixel-perfect resolution isn't necessary.  Only use the cached path if
            // the file actually exists (TryGetCachedPath always sets the out param).
            string loadPath = oldImagePath;
            if (Services.ThumbnailCacheService.TryGetCachedPath(oldImagePath, out var cachedPath))
                loadPath = cachedPath;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource        = new Uri(loadPath);
            bmp.DecodePixelWidth = (int)monitorBounds.Width;
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            OldImage.Source = bmp;
            _imageLoaded = true;
        }
        catch
        {
            // If the image can't be loaded just close immediately — better no transition
            // than an empty black flash.
            _imageLoaded = false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Use SetWindowPos with physical screen coordinates so the overlay covers the
        // correct monitor regardless of per-monitor DPI scaling.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        // HWND_BOTTOM places the window below all normal app windows
        // but above the desktop/wallpaper — so transitions don't cover other apps.
        SetWindowPos(hwnd, HWND_BOTTOM,
            (int)_monitorBounds.Left, (int)_monitorBounds.Top,
            (int)_monitorBounds.Width, (int)_monitorBounds.Height,
            SWP_NOACTIVATE);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (!_imageLoaded)
        {
            Close();
            return;
        }

        // Fade the overlay out — revealing the new wallpaper underneath.
        var anim = new DoubleAnimation(1.0, 0.0,
            new Duration(TimeSpan.FromMilliseconds(_durationMs)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        anim.Completed += (_, _) => Close();
        OldImage.BeginAnimation(OpacityProperty, anim);
    }
}
