using System.IO;
using System.Runtime.InteropServices;
using BackgroundSlideShow.Models;
using BackgroundSlideShow;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BackgroundSlideShow.Services;

public class WallpaperService
{
    // ── IDesktopWallpaper COM ─────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID,
                          [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        [return: MarshalAs(UnmanagedType.U4)]
        uint GetMonitorDevicePathCount();

        // [out] parameter — NOT a return value
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                            out RECT displayRect);

        void SetBackgroundColor([MarshalAs(UnmanagedType.U4)] uint color);

        [return: MarshalAs(UnmanagedType.U4)]
        uint GetBackgroundColor();

        void SetPosition([MarshalAs(UnmanagedType.I4)] DesktopWallpaperPosition position);

        [return: MarshalAs(UnmanagedType.I4)]
        DesktopWallpaperPosition GetPosition();

        void SetSlideshow(IntPtr items);
        IntPtr GetSlideshow();

        void SetSlideshowOptions(DesktopSlideshowOptions options,
                                 [MarshalAs(UnmanagedType.U4)] uint slideshowTick);

        void GetSlideshowOptions(out DesktopSlideshowOptions options,
                                 [MarshalAs(UnmanagedType.U4)] out uint slideshowTick);

        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                              [MarshalAs(UnmanagedType.I4)] DesktopSlideshowDirection direction);

        [return: MarshalAs(UnmanagedType.I4)]
        DesktopSlideshowState GetStatus();

        bool Enable();
    }

    private enum DesktopWallpaperPosition
    {
        Center = 0,
        Tile = 1,
        Stretch = 2,
        Fit = 3,
        Fill = 4,
        Span = 5,
    }

    private enum DesktopSlideshowOptions { None = 0, ShuffleImages = 1 }
    private enum DesktopSlideshowDirection { Forward = 0, Backward = 1 }
    private enum DesktopSlideshowState { Enabled = 1, Slideshow = 2, DisabledByRemoteSession = 4 }

    [ComImport]
    [Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass { }

    // Images wider or taller than this pixel count may exceed the GPU's maximum
    // texture dimension (commonly 8192 on mid-range hardware), causing Windows to
    // silently ignore the SetWallpaper call.  Any image exceeding this threshold
    // is scaled down to fit before being passed to the COM API.
    private const int MaxWallpaperDimension = 8192;

    // Temp directory for scaled-down wallpaper intermediates.
    private static readonly string ScaledTempDir = Path.Combine(
        Path.GetTempPath(), "BackgroundSlideShow");

    // Returns a stable per-monitor path for the scaled temp JPEG.
    // Using a fixed path (not a GUID) means we overwrite rather than delete-and-recreate,
    // which avoids a race where DWM reads the file asynchronously after SetWallpaper returns.
    private static string GetStableTempPath(string? monitorDevicePath)
    {
        // Hash the device path so multi-monitor setups get separate files.
        string suffix = monitorDevicePath is null
            ? "all"
            : ((uint)monitorDevicePath.GetHashCode()).ToString("X8");
        return Path.Combine(ScaledTempDir, $"wallpaper_{suffix}.jpg");
    }

    // Tracks monitors for which GetMonitorRECT has already logged a warning,
    // so the known Windows 11 E_FAIL is only reported once per session.
    private readonly HashSet<uint> _rectWarnedMonitors = [];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets wallpaper for a specific monitor. Pass null monitorDevicePath to target all monitors.
    /// <para>
    /// <b>Windows API limitation:</b> <c>IDesktopWallpaper.SetPosition</c> sets the fit mode
    /// <em>globally</em> for all monitors — there is no per-monitor overload. In a multi-monitor
    /// setup where different monitors have different FitMode settings, whichever call runs last
    /// will overwrite the fit mode for every monitor. This cannot be worked around through this API.
    /// </para>
    /// </summary>
    public void SetWallpaper(string? monitorDevicePath, string imagePath, FitMode fit = FitMode.Fill)
    {
        AppLogger.Info($"SetWallpaper → monitor={monitorDevicePath ?? "all"} fit={fit} path={imagePath}");

        // Scale oversized images into a stable per-monitor temp file before handing the
        // path to Windows.  IDesktopWallpaper.SetWallpaper is an IPC call into dwm.exe,
        // which returns as soon as the path is registered — DWM reads and renders the file
        // asynchronously afterwards.  Deleting (or reusing a GUID temp file) immediately
        // after the COM call races with DWM's file read and produces a partially-painted or
        // blank wallpaper.  Using a stable path (overwritten rather than deleted) means the
        // file always exists when DWM comes to read it.
        string stableTempPath = GetStableTempPath(monitorDevicePath);
        bool scaled = TryScaleOversized(imagePath, stableTempPath) is not null;
        string effectivePath = scaled ? stableTempPath : imagePath;
        if (scaled)
            AppLogger.Info($"SetWallpaper: scaled oversized image to temp → {stableTempPath}");

        var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
        try
        {
            wallpaper.SetWallpaper(monitorDevicePath, effectivePath);
            wallpaper.SetPosition(ToDesktopPosition(fit));
            AppLogger.Info($"SetWallpaper complete → {Path.GetFileName(imagePath)}");
        }
        catch (COMException ex)
        {
            AppLogger.Error($"SetWallpaper COM error (HRESULT 0x{ex.HResult:X8}) for '{imagePath}'", ex);
            throw;
        }
        finally
        {
            Marshal.ReleaseComObject(wallpaper);
        }
    }

    /// <summary>
    /// If <paramref name="imagePath"/> has a dimension exceeding <see cref="MaxWallpaperDimension"/>,
    /// writes a uniformly scaled JPEG to <paramref name="destPath"/> and returns <paramref name="destPath"/>.
    /// Returns null when the image is within the limit (no file is written).
    /// </summary>
    private static string? TryScaleOversized(string imagePath, string destPath)
    {
        // HEIC/HEIF requires WIC since ImageSharp has no HEIC codec.
        if (WicHelper.IsHeic(imagePath))
            return TryScaleOversizedViaWic(imagePath, destPath);

        ImageInfo? info;
        try { info = Image.Identify(imagePath); }
        catch (Exception ex)
        {
            AppLogger.Warn($"SetWallpaper: could not identify dimensions of '{imagePath}': {ex.Message}");
            return null; // proceed with original; let Windows decide
        }

        if (info.Width <= MaxWallpaperDimension && info.Height <= MaxWallpaperDimension)
            return null;

        double scale = Math.Min(
            (double)MaxWallpaperDimension / info.Width,
            (double)MaxWallpaperDimension / info.Height);
        int targetW = Math.Max(1, (int)(info.Width  * scale));
        int targetH = Math.Max(1, (int)(info.Height * scale));

        AppLogger.Info($"SetWallpaper: image {info.Width}×{info.Height} exceeds {MaxWallpaperDimension}px limit; scaling to {targetW}×{targetH}");

        Directory.CreateDirectory(ScaledTempDir);

        // TargetSize tells the JPEG decoder to apply DCT subsampling, avoiding allocation
        // of the full source resolution before resizing (halves peak RAM for a 16K→8K JPEG).
        // For PNG/WebP/BMP the hint is ignored, but Rgb24 (3 bytes/px) still saves ~25%
        // compared to the default Rgba32 (4 bytes/px) — e.g. ~576 MB vs ~768 MB for 16K PNG.
        var opts = new DecoderOptions { TargetSize = new Size(targetW, targetH) };
        using var img = Image.Load<Rgb24>(opts, imagePath);
        img.Mutate(x => x.Resize(targetW, targetH, KnownResamplers.Lanczos3));
        img.Save(destPath, new JpegEncoder { Quality = 95 });

        return destPath;
    }

    /// <summary>
    /// Variant of <see cref="TryScaleOversized"/> for HEIC/HEIF files, which require WIC decoding.
    /// </summary>
    private static string? TryScaleOversizedViaWic(string imagePath, string destPath)
    {
        int w, h;
        try { (w, h) = WicHelper.GetDimensions(imagePath); }
        catch (Exception ex)
        {
            AppLogger.Warn($"SetWallpaper: could not identify HEIC dimensions of '{imagePath}': {ex.Message}");
            return null;
        }

        if (w <= MaxWallpaperDimension && h <= MaxWallpaperDimension)
            return null;

        double scale = Math.Min(
            (double)MaxWallpaperDimension / w,
            (double)MaxWallpaperDimension / h);
        int targetW = Math.Max(1, (int)(w * scale));
        int targetH = Math.Max(1, (int)(h * scale));

        AppLogger.Info($"SetWallpaper: HEIC image {w}×{h} exceeds {MaxWallpaperDimension}px limit; scaling to {targetW}×{targetH}");

        Directory.CreateDirectory(ScaledTempDir);

        try
        {
            WicHelper.DecodeResizeSaveAsJpeg(imagePath, targetW, targetH, destPath, quality: 95);
            return destPath;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SetWallpaper: WIC scale failed for '{imagePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns IDesktopWallpaper device paths paired with their screen bounds.
    /// <para>
    /// <c>GetMonitorRECT</c> returns E_FAIL on some Windows 11 systems. When that happens the
    /// path is still included with <c>Rect.Empty</c> bounds so that <see cref="MonitorService"/>
    /// can fall back to positional (index-based) monitor matching.
    /// </para>
    /// </summary>
    public IReadOnlyList<(string DevicePath, System.Windows.Rect Bounds)> GetWallpaperMonitors()
    {
        try
        {
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            try
            {
                var count = wallpaper.GetMonitorDevicePathCount();
                var result = new List<(string, System.Windows.Rect)>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var path = wallpaper.GetMonitorDevicePathAt(i);
                    System.Windows.Rect bounds;
                    try
                    {
                        wallpaper.GetMonitorRECT(path, out var r);
                        bounds = new System.Windows.Rect(
                            r.Left, r.Top,
                            r.Right - r.Left, r.Bottom - r.Top);
                    }
                    catch (Exception rectEx)
                    {
                        // E_FAIL from GetMonitorRECT is a known Windows 11 issue on some systems.
                        // Log once per monitor per session to avoid spamming the log file.
                        if (_rectWarnedMonitors.Add(i))
                            AppLogger.Warn($"GetMonitorRECT failed for monitor {i} (will use index-based fallback): {rectEx.Message}");
                        bounds = System.Windows.Rect.Empty;
                    }
                    result.Add((path, bounds));
                }
                return result;
            }
            finally
            {
                Marshal.ReleaseComObject(wallpaper);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("GetWallpaperMonitors: COM error", ex);
            return Array.Empty<(string, System.Windows.Rect)>();
        }
    }

    private static DesktopWallpaperPosition ToDesktopPosition(FitMode fit) => fit switch
    {
        FitMode.Fill    => DesktopWallpaperPosition.Fill,
        FitMode.Fit     => DesktopWallpaperPosition.Fit,
        FitMode.Stretch => DesktopWallpaperPosition.Stretch,
        FitMode.Tile    => DesktopWallpaperPosition.Tile,
        FitMode.Center  => DesktopWallpaperPosition.Center,
        _               => DesktopWallpaperPosition.Fill,
    };
}
