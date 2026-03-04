using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackgroundSlideShow.Models;
using BackgroundSlideShow.Services;
using Microsoft.Win32;

namespace BackgroundSlideShow.ViewModels;

/// <summary>Manages the folder sidebar: add, remove, scan, and enable/disable folders.</summary>
public partial class FolderListViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService _libraryService;
    private readonly EventHandler _libraryChangedHandler;

    [ObservableProperty] private ObservableCollection<FolderItemViewModel> _folders = new();
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = string.Empty;

    public FolderListViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService;
        _libraryChangedHandler = async (_, _) => await RefreshFoldersAsync();
        _libraryService.LibraryChanged += _libraryChangedHandler;
    }

    public void Dispose()
    {
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

    // ── Refresh ───────────────────────────────────────────────────────────────

    public async Task RefreshFoldersAsync()
    {
        var list = await _libraryService.GetFoldersWithCountAsync();
        var vms = list.Select(t => new FolderItemViewModel(
                t.Folder, t.ImageCount,
                async vm => await _libraryService.SetFolderEnabledAsync(vm.Id, vm.IsEnabled)))
            .ToList();

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Folders.Clear();
            foreach (var vm in vms) Folders.Add(vm);
        });
    }
}
