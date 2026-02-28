using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackgroundSlideShow.Models;
using BackgroundSlideShow.Services;

namespace BackgroundSlideShow.ViewModels;

/// <summary>
/// Manages the image gallery: filtering, searching, sorting, thumbnail sizing,
/// monitor-pool filtering, and the click-to-preview overlay.
/// </summary>
public partial class ImageGalleryViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService _libraryService;
    private readonly EventHandler _libraryChangedHandler;

    // ── Gallery monitor filter ────────────────────────────────────────────────

    [ObservableProperty] private List<MonitorFilterOption> _galleryMonitorOptions =
        [MonitorFilterOption.AllMonitors];

    [ObservableProperty] private MonitorFilterOption _selectedGalleryMonitorFilter =
        MonitorFilterOption.AllMonitors;

    /// <summary>Called by LibraryViewModel after monitors are refreshed.</summary>
    public void SetMonitors(IEnumerable<MonitorViewModel> monitors)
    {
        var options = new List<MonitorFilterOption> { MonitorFilterOption.AllMonitors };
        options.AddRange(monitors.Select(m => new MonitorFilterOption(m.DisplayName, m)));
        App.Current.Dispatcher.Invoke(() =>
        {
            GalleryMonitorOptions = options;
            SelectedGalleryMonitorFilter = options[0];
        });
    }

    partial void OnSelectedGalleryMonitorFilterChanged(MonitorFilterOption value) =>
        _ = RefreshImagesAsync();

    // ── Filter / Search / Sort ────────────────────────────────────────────────

    [ObservableProperty] private string _orientationFilter = "All";
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _sortOrder = "Default";

    partial void OnOrientationFilterChanged(string value) => _ = RefreshImagesAsync();
    partial void OnSearchQueryChanged(string value)       => _ = RefreshImagesAsync();
    partial void OnSortOrderChanged(string value)         => _ = RefreshImagesAsync();

    // ── Thumbnail size (48–200 px) ────────────────────────────────────────────

    [ObservableProperty] private int _thumbnailSize = 64;

    /// <summary>Image element size = cell size minus 4 px margin.</summary>
    public int ThumbnailImageSize => ThumbnailSize - 4;

    partial void OnThumbnailSizeChanged(int value) =>
        OnPropertyChanged(nameof(ThumbnailImageSize));

    // ── Images ────────────────────────────────────────────────────────────────

    [ObservableProperty] private List<ImageEntry> _images = new();

    // ── Click-to-preview ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewFileName))]
    [NotifyPropertyChangedFor(nameof(PreviewDimensions))]
    [NotifyPropertyChangedFor(nameof(PreviewFileSize))]
    [NotifyPropertyChangedFor(nameof(PreviewPath))]
    private ImageEntry? _selectedImage;

    public bool IsPreviewVisible  => SelectedImage is not null;
    public string PreviewFileName => SelectedImage is not null ? Path.GetFileName(SelectedImage.FilePath) : string.Empty;
    public string PreviewDimensions => SelectedImage is not null ? $"{SelectedImage.Width} × {SelectedImage.Height} px" : string.Empty;
    public string PreviewFileSize => SelectedImage is not null ? FormatFileSize(SelectedImage.FileSize) : string.Empty;
    public string PreviewPath     => SelectedImage?.FilePath ?? string.Empty;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ImageGalleryViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService;
        _libraryChangedHandler = async (_, _) => await RefreshImagesAsync();
        _libraryService.LibraryChanged += _libraryChangedHandler;
    }

    public void Dispose()
    {
        _libraryService.LibraryChanged -= _libraryChangedHandler;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ClosePreview() => SelectedImage = null;

    [RelayCommand]
    public async Task ToggleExcludeImageAsync(ImageEntry image)
    {
        await _libraryService.SetImageExcludedAsync(image.Id, !image.IsExcluded);
        // LibraryChanged fires → RefreshImagesAsync runs automatically
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    public async Task RefreshImagesAsync()
    {
        // Orientation, search, and sort are pushed to the DB query.
        var filtered = await _libraryService.GetFilteredImagesAsync(
            OrientationFilter, SearchQuery, SortOrder);

        // Monitor pool filter is applied in-memory (depends on runtime MonitorViewModel state).
        IEnumerable<ImageEntry> result = filtered;
        if (SelectedGalleryMonitorFilter.Monitor is MonitorViewModel mon)
            result = ImagePoolFilter.FilterExact(result, mon.Config.ImagePoolMode, mon.IsPortrait);

        var list = result.ToList();
        App.Current.Dispatcher.Invoke(() => Images = list);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)              return $"{bytes} B";
        if (bytes < 1024 * 1024)      return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
