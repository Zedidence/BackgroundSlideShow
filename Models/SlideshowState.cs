namespace BackgroundSlideShow.Models;

public enum SlideshowStatus { Stopped, Playing, Paused }

public class SlideshowState
{
    public string MonitorId { get; set; } = string.Empty;
    public SlideshowStatus Status { get; set; } = SlideshowStatus.Stopped;
    public ImageEntry? CurrentImage { get; set; }
    public DateTime? NextChangeAt { get; set; }

    public TimeSpan TimeUntilNext =>
        NextChangeAt.HasValue ? NextChangeAt.Value - DateTime.UtcNow : TimeSpan.Zero;
}
