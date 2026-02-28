namespace BackgroundSlideShow.Models;

/// <summary>
/// Structured progress report emitted by LibraryService during folder scans.
/// </summary>
public record ScanProgress(int Current, int Total, string FileName);