using System.Runtime.InteropServices;

namespace BackgroundSlideShow.Services;

public record MonitorInfo(
    string DeviceId,
    string WallpaperDevicePath,
    System.Windows.Rect Bounds,
    bool IsPrimary)
{
    public bool IsLandscape => Bounds.Width >= Bounds.Height;
    public bool IsPortrait => Bounds.Height > Bounds.Width;
}

public class MonitorService
{
    private readonly WallpaperService _wallpaperService;

    public MonitorService(WallpaperService wallpaperService)
    {
        _wallpaperService = wallpaperService;
    }

    // ── Win32 P/Invoke ────────────────────────────────────────────────────────

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    // ── Public API ────────────────────────────────────────────────────────────

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        // Get IDesktopWallpaper device paths. Bounds may be Rect.Empty if GetMonitorRECT failed.
        var wallpaperMonitors = _wallpaperService.GetWallpaperMonitors();
        bool boundsAvailable = wallpaperMonitors.Any(w => !w.Bounds.IsEmpty);

        // Collect raw GDI monitor info without wallpaper paths yet
        var raw = new List<(string DeviceId, System.Windows.Rect Bounds, bool IsPrimary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, EnumMonitorCallback, IntPtr.Zero);

        // Sort: primary first, then left-to-right (matches IDesktopWallpaper enumeration order)
        raw.Sort((a, b) =>
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            return a.Bounds.Left.CompareTo(b.Bounds.Left);
        });

        // Assign wallpaper device paths after sorting so index-based fallback is stable
        var monitors = new List<MonitorInfo>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var (deviceId, bounds, isPrimary) = raw[i];
            string wallpaperPath;

            if (boundsAvailable)
            {
                // Normal path: match by RECT overlap
                var match = wallpaperMonitors.FirstOrDefault(w => RectsIntersect(w.Bounds, bounds));
                wallpaperPath = match.DevicePath ?? string.Empty;
            }
            else if (i < wallpaperMonitors.Count)
            {
                // Fallback: GetMonitorRECT failed for all monitors (known Windows 11 bug).
                // Both GDI and IDesktopWallpaper enumerate in primary-first, left-to-right order,
                // so positional assignment is correct.
                if (i == 0)
                    AppLogger.Warn("MonitorService: GetMonitorRECT returned empty bounds for all monitors — using index-based wallpaper path assignment (Windows 11 bug workaround)");
                wallpaperPath = wallpaperMonitors[i].DevicePath;
            }
            else
            {
                wallpaperPath = string.Empty;
            }

            monitors.Add(new MonitorInfo(deviceId, wallpaperPath, bounds, isPrimary));
        }

        return monitors;

        bool EnumMonitorCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                raw.Add((
                    info.szDevice,
                    new System.Windows.Rect(
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }
    }

    private static bool RectsIntersect(System.Windows.Rect a, System.Windows.Rect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
}
