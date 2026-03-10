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
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            GalleryMonitorOptions = options;
            SelectedGalleryMonitorFilter = options[0];
        });
    }

    partial void OnSelectedGalleryMonitorFilterChanged(MonitorFilterOption value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        _ = RefreshImagesAsync();
    }

    // ── Filter / Search / Sort ────────────────────────────────────────────────

    [ObservableProperty] private string _orientationFilter = "All";
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _sortOrder = "Default";

    partial void OnOrientationFilterChanged(string value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        _ = RefreshImagesAsync();
    }

    private CancellationTokenSource? _searchDebounce;

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        // Debounce: wait 300 ms after the last keystroke before hitting the DB.
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = new CancellationTokenSource();
        var token = _searchDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                await RefreshImagesAsync();
            }
            catch (OperationCanceledException) { }
        });
    }

    partial void OnSortOrderChanged(string value) => _ = RefreshImagesAsync();

    // ── Thumbnail size (48–200 px) ────────────────────────────────────────────

    [ObservableProperty] private int _thumbnailSize = 64;

    /// <summary>Image element size = cell size minus 4 px margin.</summary>
    public int ThumbnailImageSize => ThumbnailSize - 4;

    partial void OnThumbnailSizeChanged(int value) =>
        OnPropertyChanged(nameof(ThumbnailImageSize));

    // ── Images + counts ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredCount))]
    [NotifyPropertyChangedFor(nameof(CountSummaryText))]
    [NotifyPropertyChangedFor(nameof(IsFilterEmpty))]
    [NotifyPropertyChangedFor(nameof(PreviewPositionText))]
    private List<ImageEntry> _images = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryEmpty))]
    [NotifyPropertyChangedFor(nameof(IsFilterEmpty))]
    [NotifyPropertyChangedFor(nameof(CountSummaryText))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExcludedBadgeText))]
    private int _excludedCount;

    public int    FilteredCount    => Images.Count;
    public bool   IsLibraryEmpty   => TotalCount == 0;
    public bool   IsFilterEmpty    => Images.Count == 0 && TotalCount > 0;
    public bool   HasActiveFilters =>
        OrientationFilter != "All" ||
        !string.IsNullOrEmpty(SearchQuery) ||
        SelectedGalleryMonitorFilter != MonitorFilterOption.AllMonitors;

    public string CountSummaryText =>
        TotalCount == 0           ? string.Empty :
        FilteredCount < TotalCount ? $"Showing {FilteredCount:N0} of {TotalCount:N0}" :
                                     $"{TotalCount:N0} images";

    public string ExcludedBadgeText =>
        ExcludedCount > 0 ? $"{ExcludedCount:N0} excluded" : string.Empty;

    public bool ShowExcludedBadge => ExcludedCount > 0;

    // ── Click-to-preview ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewFileName))]
    [NotifyPropertyChangedFor(nameof(PreviewDimensions))]
    [NotifyPropertyChangedFor(nameof(PreviewFileSize))]
    [NotifyPropertyChangedFor(nameof(PreviewPath))]
    [NotifyPropertyChangedFor(nameof(PreviewPositionText))]
    [NotifyPropertyChangedFor(nameof(CanNavigatePrevious))]
    [NotifyPropertyChangedFor(nameof(CanNavigateNext))]
    private ImageEntry? _selectedImage;

    public bool   IsPreviewVisible  => SelectedImage is not null;
    public string PreviewFileName   => SelectedImage is not null ? Path.GetFileName(SelectedImage.FilePath) : string.Empty;
    public string PreviewDimensions => SelectedImage is not null ? $"{SelectedImage.Width} × {SelectedImage.Height} px" : string.Empty;
    public string PreviewFileSize   => SelectedImage is not null ? FormatFileSize(SelectedImage.FileSize) : string.Empty;
    public string PreviewPath       => SelectedImage?.FilePath ?? string.Empty;

    public string PreviewPositionText
    {
        get
        {
            if (SelectedImage is null || Images.Count == 0) return string.Empty;
            var idx = Images.IndexOf(SelectedImage);
            return idx >= 0 ? $"{idx + 1} / {Images.Count}" : string.Empty;
        }
    }

    public bool CanNavigatePrevious => SelectedImage is not null && Images.Count > 1;
    public bool CanNavigateNext     => SelectedImage is not null && Images.Count > 1;

    // ── Constructor ───────────────────────────────────────────────────────────

    private CancellationTokenSource? _refreshCts;

    public ImageGalleryViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService;
        _libraryChangedHandler = async (_, _) => await RefreshImagesAsync();
        _libraryService.LibraryChanged += _libraryChangedHandler;
    }

    public void Dispose()
    {
        _libraryService.LibraryChanged -= _libraryChangedHandler;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ClosePreview() => SelectedImage = null;

    [RelayCommand]
    private void NavigatePrevious()
    {
        if (SelectedImage is null || Images.Count == 0) return;
        var idx = Images.IndexOf(SelectedImage);
        SelectedImage = idx > 0 ? Images[idx - 1] : Images[^1];
    }

    [RelayCommand]
    private void NavigateNext()
    {
        if (SelectedImage is null || Images.Count == 0) return;
        var idx = Images.IndexOf(SelectedImage);
        SelectedImage = idx < Images.Count - 1 ? Images[idx + 1] : Images[0];
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchQuery = string.Empty;
        OrientationFilter = "All";
        SelectedGalleryMonitorFilter = GalleryMonitorOptions.Count > 0
            ? GalleryMonitorOptions[0]
            : MonitorFilterOption.AllMonitors;
    }

    [RelayCommand]
    public async Task ToggleExcludeImageAsync(ImageEntry image)
    {
        await _libraryService.SetImageExcludedAsync(image.Id, !image.IsExcluded);
        // LibraryChanged fires → RefreshImagesAsync runs automatically
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    public async Task RefreshImagesAsync()
    {
        // Cancel any already-in-flight refresh so rapid LibraryChanged events don't pile up.
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;

        try
        {
            // Run both queries concurrently; counts use cheap COUNT(*) without loading objects.
            var filteredTask = _libraryService.GetFilteredImagesAsync(
                OrientationFilter, SearchQuery, SortOrder, cts.Token);
            var countsTask = _libraryService.GetImageCountsAsync(cts.Token);

            await Task.WhenAll(filteredTask, countsTask);
            cts.Token.ThrowIfCancellationRequested();

            // Guard against the TOCTOU window between the cancellation check above and the
            // Dispatcher.Invoke below: a newer refresh may have started (replacing _refreshCts)
            // after our queries returned but before we applied the results.
            if (!ReferenceEquals(_refreshCts, cts)) return;

            var filtered = await filteredTask;
            var (total, excluded) = await countsTask;

            IEnumerable<ImageEntry> result = filtered;
            if (SelectedGalleryMonitorFilter.Monitor is MonitorViewModel mon)
                result = ImagePoolFilter.FilterExact(result, mon.Config.ImagePoolMode, mon.IsPortrait);

            var list = result.ToList();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                TotalCount    = total;
                ExcludedCount = excluded;
                Images        = list;
                OnPropertyChanged(nameof(ShowExcludedBadge));
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer refresh */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)                return $"{bytes} B";
        if (bytes < 1024 * 1024)         return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
