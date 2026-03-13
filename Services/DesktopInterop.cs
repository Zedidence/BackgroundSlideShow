using System.Runtime.InteropServices;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Locates the desktop icon layer so transition overlays can be z-ordered directly
/// below it — visible on the desktop but beneath icons, taskbar, and app windows.
///
/// On first call, sends the undocumented Progman 0x052C message to ensure the desktop
/// is in the transparent-composited hierarchy used by Win10/11:
///   WorkerW (SHELLDLL_DefView + icons) → Progman (empty) → WorkerW (empty)
/// Without this, Progman may paint the wallpaper opaquely in its own client area,
/// hiding any windows positioned below it.
/// </summary>
internal static class DesktopInterop
{
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg,
        UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint SMTO_NORMAL = 0x0000;
    private static bool _initialized;
    private static readonly object _lock = new();

    /// <summary>
    /// Returns the HWND of the top-level window that contains SHELLDLL_DefView
    /// (the desktop icon list).  On Win10/11 after the 0x052C initialization this
    /// is a WorkerW; on stock desktops it may be Progman itself.
    ///
    /// Transition windows placed directly below this HWND via SetWindowPos are:
    ///   • Visible through the desktop's DWM-transparent background
    ///   • Below all desktop icons (SHELLDLL_DefView child windows)
    ///   • Below the taskbar and all application windows
    ///
    /// Returns <see cref="IntPtr.Zero"/> if the desktop shell isn't running.
    /// </summary>
    public static IntPtr FindDesktopIconLayer()
    {
        EnsureInitialized();

        IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                result = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Sends the 0x052C message to Progman once per application lifetime.
    /// This transitions the desktop into a composited hierarchy where Progman's
    /// client area is transparent (wallpaper drawn by DWM below all HWNDs) and
    /// SHELLDLL_DefView lives inside a WorkerW above any content we insert.
    /// Idempotent — safe to call even if another app already sent the message.
    /// </summary>
    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            var progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                SendMessageTimeout(progman, 0x052C, UIntPtr.Zero, IntPtr.Zero,
                    SMTO_NORMAL, 1000, out _);
                AppLogger.Info("DesktopInterop: sent 0x052C to Progman");
            }
            else
            {
                AppLogger.Warn("DesktopInterop: Progman window not found");
            }

            _initialized = true;
        }
    }
}
