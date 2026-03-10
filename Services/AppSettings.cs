using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using BackgroundSlideShow;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Persists user-level application settings.
/// — LaunchOnStartup: stored in the Windows Run registry key.
/// — HasShownTrayHint, TransitionsEnabled, TransitionDurationMs: stored in a JSON file next to the SQLite database.
/// </summary>
public class AppSettings
{
    public static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BackgroundSlideShow");

    private static readonly string SettingsPath = Path.Combine(AppDataFolder, "settings.json");

    private const string StartupRegistryKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "BackgroundSlideShow";

    // ── Properties ────────────────────────────────────────────────────────────

    public bool HasShownTrayHint { get; set; }

    /// <summary>Whether to show a crossfade overlay when the wallpaper changes.</summary>
    public bool TransitionsEnabled { get; set; } = true;

    /// <summary>Duration of the crossfade animation in milliseconds (200–1500).</summary>
    public int TransitionDurationMs { get; set; } = 600;

    /// <summary>Folder containing GIF files for GIF Wallpaper Mode.</summary>
    public string GifFolderPath { get; set; } = "";

    /// <summary>How many seconds to display each GIF before cycling to the next (3–120).</summary>
    public int GifSecondsPerFile { get; set; } = 15;

    /// <summary>
    /// Gets or sets whether this app is registered to launch at Windows startup.
    /// Reads/writes the HKCU Run registry key directly (no caching).
    /// </summary>
    public bool LaunchOnStartup
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey);
            return key?.GetValue(AppName) is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
            if (key is null) return;
            if (value)
                key.SetValue(AppName, Environment.ProcessPath ?? string.Empty);
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(SettingsPath));
            if (data is null) return;
            HasShownTrayHint     = data.HasShownTrayHint;
            TransitionsEnabled   = data.TransitionsEnabled;
            TransitionDurationMs = data.TransitionDurationMs;
            GifFolderPath        = data.GifFolderPath;
            GifSecondsPerFile    = data.GifSecondsPerFile;
        }
        catch (Exception ex) { AppLogger.Warn($"Settings load failed — using defaults. {ex.Message}"); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(
                new SettingsData
                {
                    HasShownTrayHint     = HasShownTrayHint,
                    TransitionsEnabled   = TransitionsEnabled,
                    TransitionDurationMs = TransitionDurationMs,
                    GifFolderPath        = GifFolderPath,
                    GifSecondsPerFile    = GifSecondsPerFile,
                },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) { AppLogger.Warn($"Settings save failed. {ex.Message}"); }
    }

    private sealed class SettingsData
    {
        public bool   HasShownTrayHint     { get; set; }
        public bool   TransitionsEnabled   { get; set; } = true;
        public int    TransitionDurationMs { get; set; } = 600;
        public string GifFolderPath        { get; set; } = "";
        public int    GifSecondsPerFile    { get; set; } = 15;
    }
}
