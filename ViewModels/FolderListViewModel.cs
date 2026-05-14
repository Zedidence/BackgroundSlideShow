using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackgroundSlideShow.Models;
using BackgroundSlideShow.Services;
using Microsoft.Win32;

namespace BackgroundSlideShow.ViewModels;

public enum FolderSortMode { Name, ImageCount, LastScanned }

/// <summary>Manages the folder sidebar: add, remove, scan, and enable/disable folders.</summary>
public partial class FolderListViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService _libraryService;
    private readonly EventHandler _libraryChangedHandler;

    [ObservableProperty] private ObservableCollection<FolderItemViewModel> _folders = new();
    [ObservableProperty] private bool   _isScanning;
    [ObservableProperty] private string _scanStatus = string.Empty;
    [ObservableProperty] private FolderSortMode _currentSort = FolderSortMode.Name;
    [ObservableProperty] private bool _sortAscending = true;

    /// <summary>One-line summary shown in the sidebar header, e.g. "4 folders · 1,234 images".</summary>
    public string SummaryText
    {
        get
        {
            if (Folders.Count == 0) return string.Empty;
            var folderWord = Folders.Count == 1 ? "folder" : "folders";
            var total      = Folders.Sum(f => f.ImageCount);
            return $"{Folders.Count} {folderWord} · {total:N0} images";
        }
    }

    public IEnumerable<FolderItemViewModel> SortedFolders =>
        (CurrentSort, SortAscending) switch
        {
            (FolderSortMode.Name, true)        => Folders.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase),
            (FolderSortMode.Name, false)       => Folders.OrderByDescending(f => f.DisplayName, StringComparer.OrdinalIgnoreCase),
            (FolderSortMode.ImageCount, true)  => Folders.OrderBy(f => f.ImageCount),
            (FolderSortMode.ImageCount, false) => Folders.OrderByDescending(f => f.ImageCount),
            (FolderSortMode.LastScanned, true) => Folders.OrderBy(f => f.LastScanned ?? DateTime.MinValue),
            _                                  => Folders.OrderByDescending(f => f.LastScanned ?? DateTime.MinValue),
        };

    public bool IsSortedByName        => CurrentSort == FolderSortMode.Name;
    public bool IsSortedByCount       => CurrentSort == FolderSortMode.ImageCount;
    public bool IsSortedByLastScanned => CurrentSort == FolderSortMode.LastScanned;

    public FolderListViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService;
        _libraryChangedHandler = async (_, _) => await RefreshFoldersAsync();
        _libraryService.LibraryChanged += _libraryChangedHandler;
        Folders.CollectionChanged += OnFoldersCollectionChanged;
    }

    private void OnFoldersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(SortedFolders));

    partial void OnCurrentSortChanged(FolderSortMode value)
    {
        OnPropertyChanged(nameof(SortedFolders));
        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedByCount));
        OnPropertyChanged(nameof(IsSortedByLastScanned));
    }

    partial void OnSortAscendingChanged(bool value) => OnPropertyChanged(nameof(SortedFolders));

    [RelayCommand]
    private void SetSort(FolderSortMode mode)
    {
        if (CurrentSort == mode)
            SortAscending = !SortAscending;
        else
        {
            CurrentSort = mode;
            SortAscending = true;
        }
    }

    public void Dispose()
    {
        Folders.CollectionChanged -= OnFoldersCollectionChanged;
        _libraryService.LibraryChanged -= _libraryChangedHandler;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Select image folder(s)", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        await DoAddFoldersAsync(dialog.FolderNames);
    }

    /// <summary>Called from code-behind for drag-and-drop folder paths.</summary>
    public Task AddFoldersByPathAsync(IEnumerable<string> paths) => DoAddFoldersAsync(paths);

    private async Task DoAddFoldersAsync(IEnumerable<string> paths)
    {
        // Add all folders first so they appear in the list before scanning begins.
        var folders = new List<LibraryFolder>();
        foreach (var path in paths)
            folders.Add(await _libraryService.AddFolderAsync(path));

        if (folders.Count == 0) return;
        await RefreshFoldersAsync();

        IsScanning = true;
        int i = 0;
        foreach (var folder in folders)
        {
            i++;
            string prefix = folders.Count > 1 ? $"[{i}/{folders.Count}] " : string.Empty;
            var progress = new Progress<ScanProgress>(p =>
                ScanStatus = $"{prefix}Scanning: {p.Current:N0} / {p.Total:N0} — {p.FileName}");
            await _libraryService.ScanFolderAsync(folder, progress);
        }

        await RefreshFoldersAsync();
        IsScanning = false;
        ScanStatus = $"Done — {Folders.Sum(f => f.ImageCount):N0} images";
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(int folderId)
    {
        await _libraryService.RemoveFolderAsync(folderId);
        await RefreshFoldersAsync();
    }

    [RelayCommand]
    private async Task RemoveAllFoldersAsync()
    {
        await _libraryService.RemoveAllFoldersAsync();
        await RefreshFoldersAsync();
    }

    [RelayCommand]
    private async Task ScanNowAsync()
    {
        IsScanning = true;
        var progress = new Progress<ScanProgress>(p =>
            ScanStatus = $"Scanning: {p.Current:N0} / {p.Total:N0} — {p.FileName}");
        await _libraryService.ScanAllFoldersAsync(progress);
        await RefreshFoldersAsync();
        IsScanning = false;
        ScanStatus = $"Done — {Folders.Sum(f => f.ImageCount):N0} images";
    }

    /// <summary>Called on app startup to silently scan all existing folders for new/changed images.</summary>
    public async Task ScanOnStartupAsync()
    {
        var folders = await _libraryService.GetFoldersWithCountAsync();
        if (folders.Count == 0) return;
        await ScanNowAsync();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    public async Task RefreshFoldersAsync()
    {
        var list = await _libraryService.GetFoldersWithCountAsync();

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var existingById = Folders.ToDictionary(f => f.Id);
            var newIds       = new HashSet<int>(list.Select(t => t.Folder.Id));

            // Remove VMs for folders that were deleted.
            for (int i = Folders.Count - 1; i >= 0; i--)
                if (!newIds.Contains(Folders[i].Id))
                    Folders.RemoveAt(i);

            // Update existing VMs in-place; append any brand-new folders.
            foreach (var (folder, count) in list)
            {
                if (existingById.TryGetValue(folder.Id, out var vm))
                    vm.Refresh(count, folder.IsEnabled, folder.LastScanned);
                else
                    Folders.Add(new FolderItemViewModel(
                        folder, count,
                        async v => await _libraryService.SetFolderEnabledAsync(v.Id, v.IsEnabled)));
            }

            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(SortedFolders));
        });
    }
}
