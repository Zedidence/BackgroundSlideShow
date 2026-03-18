namespace BackgroundSlideShow.Models;

public enum SlideshowStatus { Stopped, Playing, Paused }

public class SlideshowState
{
    public string MonitorId { get; set; } = string.Empty;
    public SlideshowStatus Status { get; set; } = SlideshowStatus.Stopped;
    public ImageEntry? CurrentImage { get; set; }
    public DateTime? NextChangeAt { get; set; }

    /// <summary>
    /// File paths of all images currently displayed on this monitor.
    /// For a single image this contains one entry; for a collage it contains all panel images.
    /// Used for cross-monitor duplicate prevention.
    /// </summary>
    public List<string> CurrentImagePaths { get; set; } = new();

    public TimeSpan TimeUntilNext =>
        NextChangeAt.HasValue ? NextChangeAt.Value - DateTime.UtcNow : TimeSpan.Zero;
}
