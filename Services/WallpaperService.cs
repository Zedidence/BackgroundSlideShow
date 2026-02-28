using System.Runtime.InteropServices;
using BackgroundSlideShow.Models;
using BackgroundSlideShow;

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
        var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
        try
        {
            wallpaper.SetWallpaper(monitorDevicePath, imagePath);
            wallpaper.SetPosition(ToDesktopPosition(fit));
        }
        finally
        {
            Marshal.ReleaseComObject(wallpaper);
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
                        AppLogger.Error($"GetMonitorRECT failed for monitor {i} — falling back to index-based matching", rectEx);
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
