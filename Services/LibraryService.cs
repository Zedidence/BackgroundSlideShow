using System.Collections.Concurrent;
using System.IO;
using BackgroundSlideShow.Data;
using BackgroundSlideShow.Models;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;

namespace BackgroundSlideShow.Services;

public class LibraryService : ILibraryService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    private readonly AppDbContext _db;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);

    public event EventHandler? LibraryChanged;

    public LibraryService(AppDbContext db)
    {
        _db = db;
    }

    // ── Folder management ─────────────────────────────────────────────────────

    public async Task<LibraryFolder> AddFolderAsync(string path)
    {
        path = Path.GetFullPath(path);
        var folder = await _db.LibraryFolders.FirstOrDefaultAsync(f => f.Path == path);
        if (folder is null)
        {
            folder = new LibraryFolder { Path = path };
            _db.LibraryFolders.Add(folder);
            await _db.SaveChangesAsync();
        }
        AttachWatcher(folder);
        return folder;
    }

    public async Task RemoveFolderAsync(int folderId)
    {
        var folder = await _db.LibraryFolders.FindAsync(folderId);
        if (folder is null) return;
        _db.LibraryFolders.Remove(folder);
        await _db.SaveChangesAsync();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAllFoldersAsync()
    {
        var folders = await _db.LibraryFolders.ToListAsync();
        _db.LibraryFolders.RemoveRange(folders);
        await _db.SaveChangesAsync();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns all folders paired with their indexed image count.</summary>
    public async Task<List<(LibraryFolder Folder, int ImageCount)>> GetFoldersWithCountAsync()
    {
        // Use a fresh context so this read doesn't conflict with concurrent scan or save operations.
        await using var db = new AppDbContext();
        var rows = await db.LibraryFolders
            .Select(f => new { Folder = f, Count = f.Images.Count() })
            .ToListAsync();
        return rows.Select(r => (r.Folder, r.Count)).ToList();
    }

    /// <summary>Persists the IsEnabled flag for a folder.</summary>
    public async Task SetFolderEnabledAsync(int folderId, bool isEnabled)
    {
        var folder = await _db.LibraryFolders.FindAsync(folderId);
        if (folder is null) return;
        folder.IsEnabled = isEnabled;
        await _db.SaveChangesAsync();
    }

    /// <summary>Toggles the IsExcluded flag on an image entry and persists it.</summary>
    public async Task SetImageExcludedAsync(int imageId, bool isExcluded)
    {
        var image = await _db.Images.FindAsync(imageId);
        if (image is null) return;
        image.IsExcluded = isExcluded;
        await _db.SaveChangesAsync();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Folder assignments ────────────────────────────────────────────────────

    public async Task<List<int>> GetFolderAssignmentsAsync(int monitorConfigId)
    {
        await using var db = new AppDbContext();
        return await db.MonitorFolderAssignments
            .Where(a => a.MonitorConfigId == monitorConfigId)
            .Select(a => a.FolderId)
            .ToListAsync();
    }

    public async Task SetFolderAssignmentsAsync(int monitorConfigId, IEnumerable<int> folderIds)
    {
        var folderIdSet = folderIds.ToHashSet();
        var existing = await _db.MonitorFolderAssignments
            .Where(a => a.MonitorConfigId == monitorConfigId)
            .ToListAsync();

        _db.MonitorFolderAssignments.RemoveRange(existing);
        foreach (var folderId in folderIdSet)
        {
            _db.MonitorFolderAssignments.Add(new Models.MonitorFolderAssignment
            {
                MonitorConfigId = monitorConfigId,
                FolderId = folderId,
            });
        }
        await _db.SaveChangesAsync();
    }

    // ── Scanning ──────────────────────────────────────────────────────────────

    public async Task ScanAllFoldersAsync(IProgress<ScanProgress>? progress = null)
    {
        var folders = await _db.LibraryFolders.Where(f => f.IsEnabled).ToListAsync();
        foreach (var folder in folders)
            await ScanFolderAsync(folder, progress, fireEvent: false);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ScanFolderAsync(LibraryFolder folder, IProgress<ScanProgress>? progress = null,
                                      bool fireEvent = true)
    {
        if (!Directory.Exists(folder.Path)) return;

        var files = Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        var existing = await _db.Images
            .Where(i => i.LibraryFolderId == folder.Id)
            .ToDictionaryAsync(i => i.FilePath);

        var seen = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        var (toAdd, toUpdate) = ScanDiskFiles(folder.Id, files, existing, progress);
        ApplyDatabaseChanges(folder, toAdd, toUpdate, seen, existing);

        folder.LastScanned = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        if (fireEvent) LibraryChanged?.Invoke(this, EventArgs.Empty);

        // Generate disk thumbnails for new/changed images in the background.
        // This makes subsequent gallery opens significantly faster.
        var pathsToThumb = toAdd.Select(e => e.FilePath)
            .Concat(toUpdate.Select(t => t.Entry.FilePath))
            .ToList();
        if (pathsToThumb.Count > 0)
            _ = ThumbnailCacheService.GenerateBatchAsync(pathsToThumb);
    }

    /// <summary>
    /// Reads image metadata for new/changed files in parallel.
    /// Returns bags of entries to add and update.
    /// </summary>
    private static (ConcurrentBag<ImageEntry> ToAdd,
                    ConcurrentBag<(ImageEntry Entry, FileInfo Info, int W, int H)> ToUpdate)
        ScanDiskFiles(
            int folderId,
            List<string> files,
            Dictionary<string, ImageEntry> existing,
            IProgress<ScanProgress>? progress)
    {
        var toAdd    = new ConcurrentBag<ImageEntry>();
        var toUpdate = new ConcurrentBag<(ImageEntry, FileInfo, int, int)>();

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
        };

        int processed = 0;
        int total = files.Count;

        Parallel.ForEach(files, parallelOpts, file =>
        {
            int current = Interlocked.Increment(ref processed);
            progress?.Report(new ScanProgress(current, total, Path.GetFileName(file)));
            var info = new FileInfo(file);

            if (existing.TryGetValue(file, out var entry))
            {
                if (entry.LastModified != info.LastWriteTimeUtc)
                {
                    try
                    {
                        var (w, h) = ReadDimensions(file);
                        toUpdate.Add((entry, info, w, h));
                    }
                    catch { /* skip unreadable files */ }
                }
            }
            else
            {
                try
                {
                    var (w, h) = ReadDimensions(file);
                    toAdd.Add(new ImageEntry
                    {
                        FilePath         = file,
                        LibraryFolderId  = folderId,
                        Width            = w,
                        Height           = h,
                        FileSize         = info.Length,
                        LastModified     = info.LastWriteTimeUtc,
                    });
                }
                catch { /* skip unreadable files */ }
            }
        });

        return (toAdd, toUpdate);
    }

    /// <summary>
    /// Applies the results of a disk scan to the EF Core change tracker (single-threaded).
    /// </summary>
    private void ApplyDatabaseChanges(
        LibraryFolder folder,
        ConcurrentBag<ImageEntry> toAdd,
        ConcurrentBag<(ImageEntry Entry, FileInfo Info, int W, int H)> toUpdate,
        HashSet<string> seen,
        Dictionary<string, ImageEntry> existing)
    {
        foreach (var (entry, info, w, h) in toUpdate)
        {
            entry.Width        = w;
            entry.Height       = h;
            entry.FileSize     = info.Length;
            entry.LastModified = info.LastWriteTimeUtc;
        }

        foreach (var entry in toAdd)
            _db.Images.Add(entry);

        foreach (var (path, entry) in existing)
        {
            if (!seen.Contains(path))
                _db.Images.Remove(entry);
        }
    }

    private static (int Width, int Height) ReadDimensions(string path)
    {
        var info = Image.Identify(path);
        return (info.Width, info.Height);
    }

    // ── Image queries ─────────────────────────────────────────────────────────

    public async Task<List<ImageEntry>> GetAllImagesAsync(CancellationToken ct = default)
    {
        // Fresh context — this is called from a background Task in SlideshowEngine and can race with scans.
        await using var db = new AppDbContext();
        return await db.Images.ToListAsync(ct);
    }

    public Task<List<ImageEntry>> GetLandscapeImagesAsync() =>
        _db.Images.Where(i => i.Width >= i.Height).ToListAsync();

    public Task<List<ImageEntry>> GetPortraitImagesAsync() =>
        _db.Images.Where(i => i.Height > i.Width).ToListAsync();

    /// <summary>
    /// Returns images filtered and sorted by the DB engine.
    /// <para>
    /// The monitor-pool filter is not applied here because it depends on runtime
    /// <see cref="MonitorViewModel"/> state. Apply <see cref="ImagePoolFilter.FilterExact"/>
    /// on the returned list when a specific monitor is selected in the gallery.
    /// </para>
    /// </summary>
    public async Task<List<ImageEntry>> GetFilteredImagesAsync(
        string orientationFilter,
        string searchQuery,
        string sortOrder,
        CancellationToken ct = default)
    {
        // Fresh context — called from gallery VM which can race with scans triggered by LibraryChanged.
        await using var db = new AppDbContext();
        IQueryable<ImageEntry> q = db.Images;

        // Orientation
        q = orientationFilter switch
        {
            "Landscape" => q.Where(i => i.Width >= i.Height),
            "Portrait"  => q.Where(i => i.Height > i.Width),
            _           => q,
        };

        // Search
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            // SQLite LIKE is case-insensitive by default for ASCII characters.
            // Use EF.Functions.Like for a DB-side contains check.
            var pattern = $"%{searchQuery.Trim()}%";
            q = q.Where(i => EF.Functions.Like(i.FilePath, pattern));
        }

        // Sort
        q = sortOrder switch
        {
            "Name A→Z"      => q.OrderBy(i => i.FilePath),
            "Name Z→A"      => q.OrderByDescending(i => i.FilePath),
            "Date Modified" => q.OrderByDescending(i => i.LastModified),
            "File Size"     => q.OrderByDescending(i => i.FileSize),
            _               => q,
        };

        return await q.ToListAsync(ct);
    }

    // ── FileSystemWatcher ─────────────────────────────────────────────────────

    private void AttachWatcher(LibraryFolder folder)
    {
        if (!Directory.Exists(folder.Path)) return;

        var watcher = new FileSystemWatcher(folder.Path)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };

        watcher.Created += (_, e) => OnFileEvent(folder, e.FullPath);
        watcher.Deleted += (_, e) => OnFileEvent(folder, e.FullPath);
        watcher.Renamed += (_, e) => OnFileEvent(folder, e.FullPath);

        _watchers.Add(watcher);
    }

    private void OnFileEvent(LibraryFolder folder, string path)
    {
        if (!SupportedExtensions.Contains(Path.GetExtension(path))) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await _scanSemaphore.WaitAsync();
            try
            {
                await ScanFolderAsync(folder);
            }
            finally
            {
                _scanSemaphore.Release();
            }
        });
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _scanSemaphore.Dispose();
    }
}
