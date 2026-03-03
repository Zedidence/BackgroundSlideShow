using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Public contract for the library data layer — folder and image management,
/// scanning, and change notification.
/// </summary>
public interface ILibraryService : IDisposable
{
    /// <summary>Raised after any change to folders or images (scan complete, add, remove).</summary>
    event EventHandler? LibraryChanged;

    // ── Folder management ─────────────────────────────────────────────────────

    Task<LibraryFolder> AddFolderAsync(string path);
    Task RemoveFolderAsync(int folderId);
    Task RemoveAllFoldersAsync();
    Task<List<(LibraryFolder Folder, int ImageCount)>> GetFoldersWithCountAsync();
    Task SetFolderEnabledAsync(int folderId, bool isEnabled);
    Task SetImageExcludedAsync(int imageId, bool isExcluded);

    // ── Scanning ──────────────────────────────────────────────────────────────

    Task ScanAllFoldersAsync(IProgress<ScanProgress>? progress = null);
    Task ScanFolderAsync(LibraryFolder folder, IProgress<ScanProgress>? progress = null,
                         bool fireEvent = true);

    // ── Folder assignments ────────────────────────────────────────────────────

    /// <summary>Returns the IDs of folders explicitly assigned to a monitor config.</summary>
    Task<List<int>> GetFolderAssignmentsAsync(int monitorConfigId);

    /// <summary>Replaces all folder assignments for a monitor config with the given set.</summary>
    Task SetFolderAssignmentsAsync(int monitorConfigId, IEnumerable<int> folderIds);

    // ── Image queries ─────────────────────────────────────────────────────────

    Task<List<ImageEntry>> GetAllImagesAsync(CancellationToken ct = default);

    /// <summary>Returns (total image count, excluded image count) without loading image objects.</summary>
    Task<(int Total, int Excluded)> GetImageCountsAsync(CancellationToken ct = default);

    Task<List<ImageEntry>> GetFilteredImagesAsync(
        string orientationFilter,
        string searchQuery,
        string sortOrder,
        CancellationToken ct = default);
}
