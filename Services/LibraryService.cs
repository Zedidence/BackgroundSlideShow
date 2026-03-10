using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO;
using BackgroundSlideShow.Data;
using BackgroundSlideShow.Models;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;

namespace BackgroundSlideShow.Services;

public class LibraryService : ILibraryService
{
    private static readonly FrozenSet<string> SupportedExtensions =
        new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly AppDbContext _db;
    // Keyed by folder path — ensures one watcher per folder and allows targeted cleanup.
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scanSemaphore = new(1, 1);
    // Per-folder debounce: the CTS is replaced on each new file event so only the
    // last event in a burst actually triggers a rescan (prevents O(N) scans on bulk copies).
    private readonly Dictionary<string, CancellationTokenSource> _fileEventDebounce =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? LibraryChanged;

    public LibraryService(AppDbContext db)
    {
        _db = db;
    }

    // ── Folder management ─────────────────────────────────────────────────────

    public async Task<LibraryFolder> AddFolderAsync(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        var folder = await _db.LibraryFolders.FirstOrDefaultAsync(f => f.Path == path, ct);
        if (folder is null)
        {
            folder = new LibraryFolder { Path = path };
            _db.LibraryFolders.Add(folder);
            await _db.SaveChangesAsync(ct);
        }
        AttachWatcher(folder);
        return folder;
    }

    public async Task RemoveFolderAsync(int folderId)
    {
        var folder = await _db.LibraryFolders.FindAsync(folderId);
        if (folder is null) return;

        // Stop and dispose the watcher so it no longer fires events for this folder.
        if (_watchers.TryGetValue(folder.Path, out var watcher))
        {
            watcher.Dispose();
            _watchers.Remove(folder.Path);
        }

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
            .OrderBy(f => f.Path)
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

    public async Task ScanAllFoldersAsync(IProgress<ScanProgress>? progress = null,
                                          CancellationToken ct = default)
    {
        var folders = await _db.LibraryFolders.Where(f => f.IsEnabled).ToListAsync(ct).ConfigureAwait(false);
        foreach (var folder in folders)
            await ScanFolderAsync(folder, progress, fireEvent: false, ct).ConfigureAwait(false);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ScanFolderAsync(LibraryFolder folder, IProgress<ScanProgress>? progress = null,
                                      bool fireEvent = true, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder.Path)) return;

        var files = await Task.Run(() =>
            Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                .ToList(), ct).ConfigureAwait(false);

        var existing = await _db.Images
            .Where(i => i.LibraryFolderId == folder.Id)
            .ToDictionaryAsync(i => i.FilePath, ct).ConfigureAwait(false);

        var seen = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        var (toAdd, toUpdate) = await Task.Run(() => ScanDiskFiles(folder.Id, files, existing, progress, ct), ct).ConfigureAwait(false);
        ApplyDatabaseChanges(folder, toAdd, toUpdate, seen, existing);

        folder.LastScanned = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
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
            IProgress<ScanProgress>? progress,
            CancellationToken ct = default)
    {
        var toAdd    = new ConcurrentBag<ImageEntry>();
        var toUpdate = new ConcurrentBag<(ImageEntry, FileInfo, int, int)>();

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct,
        };

        int processed = 0;
        int total = files.Count;

        Parallel.ForEach(files, parallelOpts, file =>
        {
            int current = Interlocked.Increment(ref processed);
            // Throttle: only update every 100 files to avoid flooding the UI dispatcher queue.
            if (progress is not null && (current % 100 == 0 || current == total))
                progress.Report(new ScanProgress(current, total, Path.GetFileName(file)));
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
                    catch (Exception ex) { AppLogger.Warn($"Skipping unreadable file '{file}': {ex.Message}"); }
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
                catch (Exception ex) { AppLogger.Warn($"Skipping unreadable file '{file}': {ex.Message}"); }
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

    public async Task<(int Total, int Excluded)> GetImageCountsAsync(CancellationToken ct = default)
    {
        await using var db = new AppDbContext();
        var total    = await db.Images.CountAsync(ct);
        var excluded = await db.Images.CountAsync(i => i.IsExcluded, ct);
        return (total, excluded);
    }

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
        // Prevent duplicate watchers when the same folder is added more than once.
        if (_watchers.ContainsKey(folder.Path)) return;

        var watcher = new FileSystemWatcher(folder.Path)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };

        watcher.Created += (_, e) => OnFileEvent(folder, e.FullPath);
        watcher.Deleted += (_, e) => OnFileEvent(folder, e.FullPath);
        watcher.Renamed += (_, e) => OnFileEvent(folder, e.FullPath);

        _watchers[folder.Path] = watcher;
    }

    private void OnFileEvent(LibraryFolder folder, string path)
    {
        if (!SupportedExtensions.Contains(Path.GetExtension(path))) return;

        // Debounce: cancel any pending scan for this folder and schedule a fresh one.
        // Only the last event in a burst (e.g. a bulk file copy) triggers a real scan.
        lock (_fileEventDebounce)
        {
            if (_fileEventDebounce.TryGetValue(folder.Path, out var prev))
            {
                prev.Cancel();
                prev.Dispose();
            }
            var cts = new CancellationTokenSource();
            _fileEventDebounce[folder.Path] = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, cts.Token);
                    await _scanSemaphore.WaitAsync(cts.Token);
                    try
                    {
                        await ScanFolderAsync(folder);
                    }
                    finally
                    {
                        _scanSemaphore.Release();
                    }
                }
                catch (OperationCanceledException) { /* superseded by a newer event */ }
                finally
                {
                    lock (_fileEventDebounce)
                    {
                        if (_fileEventDebounce.TryGetValue(folder.Path, out var cur) && cur == cts)
                            _fileEventDebounce.Remove(folder.Path);
                        cts.Dispose();
                    }
                }
            }, CancellationToken.None); // run body unconditionally; cts is checked internally
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();

        lock (_fileEventDebounce)
        {
            foreach (var cts in _fileEventDebounce.Values) { cts.Cancel(); cts.Dispose(); }
            _fileEventDebounce.Clear();
        }

        _scanSemaphore.Dispose();
    }
}
