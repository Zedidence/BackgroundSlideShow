using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BackgroundSlideShow.Models;

public enum SlideOrder { Random, Sequential }
public enum FitMode { Fill, Fit, Stretch, Tile, Center }
public enum ImagePoolMode { All, Landscape, Portrait, Smart }

/// <summary>
/// Controls which folders contribute images to a monitor's slideshow pool.
/// All = every enabled library folder; Selected = only the folders explicitly assigned.
/// </summary>
public enum FolderAssignmentMode { All, Selected }

public class MonitorConfig : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    public int Id { get; set; }

    /// <summary>Device ID string from IDesktopWallpaper / Win32 (e.g. "\\.\DISPLAY1").</summary>
    public string MonitorId { get; set; } = string.Empty;

    private bool _isEnabled = true;
    public bool IsEnabled { get => _isEnabled; set { _isEnabled = value; Notify(); } }

    private int _intervalSeconds = 1800;
    public int IntervalSeconds { get => _intervalSeconds; set { _intervalSeconds = value; Notify(); } }

    private SlideOrder _order = SlideOrder.Random;
    public SlideOrder Order { get => _order; set { _order = value; Notify(); } }

    private FitMode _fitMode = FitMode.Fill;
    public FitMode FitMode { get => _fitMode; set { _fitMode = value; Notify(); } }

    private bool _collageEnabled = false;
    public bool CollageEnabled { get => _collageEnabled; set { _collageEnabled = value; Notify(); } }

    /// <summary>
    /// Probability (0–100) that a given wallpaper change produces a collage instead of a
    /// single image. Only meaningful when <see cref="CollageEnabled"/> is true.
    /// Default is 30 (≈ one collage every three slides).
    /// </summary>
    private int _collageChance = 30;
    public int CollageChance
    {
        get => _collageChance;
        set { _collageChance = Math.Clamp(value, 0, 100); Notify(); }
    }

    /// <summary>Controls which orientation of images are eligible for this monitor.</summary>
    private ImagePoolMode _imagePoolMode = ImagePoolMode.Smart;
    public ImagePoolMode ImagePoolMode { get => _imagePoolMode; set { _imagePoolMode = value; Notify(); } }

    /// <summary>Controls whether all library folders or only selected folders feed this monitor.</summary>
    private FolderAssignmentMode _folderAssignmentMode = FolderAssignmentMode.All;
    public FolderAssignmentMode FolderAssignmentMode
    {
        get => _folderAssignmentMode;
        set { _folderAssignmentMode = value; Notify(); }
    }
}
