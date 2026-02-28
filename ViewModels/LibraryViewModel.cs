using BackgroundSlideShow.Services;

namespace BackgroundSlideShow.ViewModels;

/// <summary>Option in the Gallery's "Filter by monitor" dropdown.</summary>
public record MonitorFilterOption(string Label, MonitorViewModel? Monitor)
{
    public static readonly MonitorFilterOption AllMonitors = new("All Monitors", null);
}

/// <summary>
/// Thin coordinator that owns the two focused sub-ViewModels surfaced by the
/// library sidebar (<see cref="FolderList"/>) and the gallery view (<see cref="Gallery"/>).
/// </summary>
public class LibraryViewModel : IDisposable
{
    public FolderListViewModel   FolderList { get; }
    public ImageGalleryViewModel Gallery    { get; }

    public LibraryViewModel(ILibraryService libraryService)
    {
        FolderList = new FolderListViewModel(libraryService);
        Gallery    = new ImageGalleryViewModel(libraryService);
    }

    /// <summary>Called by MainViewModel after monitors are refreshed.</summary>
    public void SetMonitors(IEnumerable<MonitorViewModel> monitors) =>
        Gallery.SetMonitors(monitors);

    public void Dispose()
    {
        FolderList.Dispose();
        Gallery.Dispose();
    }
}
