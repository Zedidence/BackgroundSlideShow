using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using BackgroundSlideShow;
using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Persists user-level application settings.
/// — LaunchOnStartup: stored in the Windows Run registry key (never serialized to JSON).
/// — All other properties: stored in a JSON file next to the SQLite database.
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

    /// <summary>Folder containing images for the Lock Screen Slideshow.</summary>
    public string LockScreenFolderPath { get; set; } = "";

    /// <summary>How many minutes to display each lock screen image before cycling (1–120).</summary>
    public int LockScreenIntervalMinutes { get; set; } = 30;

    /// <summary>When true, a photo collage is shown periodically (every 4–8 images) like the Windows lock screen.</summary>
    public bool LockScreenCollageEnabled { get; set; } = true;

    /// <summary>How single lock screen images are framed on the canvas.</summary>
    public FitMode LockScreenFitMode { get; set; } = FitMode.Fill;

    /// <summary>
    /// Gets or sets whether this app is registered to launch at Windows startup.
    /// Reads/writes the HKCU Run registry key directly — excluded from JSON serialization
    /// to prevent accidental registry side-effects during Load/Save.
    /// </summary>
    [JsonIgnore]
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

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (loaded is null) return;
            HasShownTrayHint          = loaded.HasShownTrayHint;
            TransitionsEnabled        = loaded.TransitionsEnabled;
            TransitionDurationMs      = loaded.TransitionDurationMs;
            GifFolderPath             = loaded.GifFolderPath;
            GifSecondsPerFile         = loaded.GifSecondsPerFile;
            LockScreenFolderPath      = loaded.LockScreenFolderPath;
            LockScreenIntervalMinutes = loaded.LockScreenIntervalMinutes;
            LockScreenCollageEnabled  = loaded.LockScreenCollageEnabled;
            LockScreenFitMode         = loaded.LockScreenFitMode;
        }
        catch (Exception ex) { AppLogger.Warn($"Settings load failed — using defaults. {ex.Message}"); }
    }

    // Save can be called from any thread (UI button handlers, background scans, etc.).
    // Serialize the writes so two callers can't truncate each other's output mid-flight.
    private static readonly object _saveLock = new();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(
                new SettingsData
                {
                    HasShownTrayHint          = HasShownTrayHint,
                    TransitionsEnabled        = TransitionsEnabled,
                    TransitionDurationMs      = TransitionDurationMs,
                    GifFolderPath             = GifFolderPath,
                    GifSecondsPerFile         = GifSecondsPerFile,
                    LockScreenFolderPath      = LockScreenFolderPath,
                    LockScreenIntervalMinutes = LockScreenIntervalMinutes,
                    LockScreenCollageEnabled  = LockScreenCollageEnabled,
                    LockScreenFitMode         = LockScreenFitMode,
                },
                new JsonSerializerOptions { WriteIndented = true });

            // Atomic write: write to a sibling temp file, then File.Move replaces the
            // target in a single filesystem operation. A crash mid-write leaves the
            // previous settings.json intact instead of producing a zero-byte JSON that
            // Load() would silently fall back to defaults for.
            var tempPath = SettingsPath + ".tmp";
            lock (_saveLock)
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsPath, overwrite: true);
            }
        }
        catch (Exception ex) { AppLogger.Warn($"Settings save failed. {ex.Message}"); }
    }

    private sealed class SettingsData
    {
        public bool   HasShownTrayHint          { get; set; }
        public bool   TransitionsEnabled        { get; set; } = true;
        public int    TransitionDurationMs      { get; set; } = 600;
        public string GifFolderPath             { get; set; } = "";
        public int    GifSecondsPerFile         { get; set; } = 15;
        public string  LockScreenFolderPath      { get; set; } = "";
        public int     LockScreenIntervalMinutes { get; set; } = 30;
        public bool    LockScreenCollageEnabled  { get; set; } = true;
        public FitMode LockScreenFitMode         { get; set; } = FitMode.Fill;
    }
}
