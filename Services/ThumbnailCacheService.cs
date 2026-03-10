using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Manages a disk-based thumbnail cache at %LOCALAPPDATA%\BackgroundSlideShow\thumbs\.
/// Thumbnails are 200 px on the longest axis, stored as JPEG quality-80.
/// The cache path for a given source image is derived from an MD5 of the lowercase
/// file path — collision-free in practice for a personal image library.
/// </summary>
public static class ThumbnailCacheService
{
    public static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BackgroundSlideShow", "thumbs");

    /// <summary>Maximum pixel dimension (width or height) of a cached thumbnail.</summary>
    public const int MaxThumbDimension = 200;

    // ── In-memory index of known cached paths ─────────────────────────────────
    // Populated at startup via PreloadCacheIndex(). After that TryGetCachedPath
    // is O(1) with zero disk I/O — critical since it runs on the UI thread for
    // every gallery cell that scrolls into view.

    private static readonly ConcurrentDictionary<string, byte> _knownCached =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Scans the thumbs directory once at startup to populate the in-memory index.
    /// Must be called before the gallery is shown.
    /// </summary>
    public static void PreloadCacheIndex()
    {
        if (!Directory.Exists(CacheDir)) return;
        foreach (var f in Directory.EnumerateFiles(CacheDir, "*.jpg"))
            _knownCached.TryAdd(f, 0);
    }

    // ── Path helpers ──────────────────────────────────────────────────────────

    public static string GetCachePath(string imagePath)
    {
        // stackalloc avoids heap allocation for UTF-8 bytes and MD5 output.
        // Called on the UI thread for every gallery cell that scrolls into view.
        var lower = imagePath.ToLowerInvariant();
        int byteCount = Encoding.UTF8.GetByteCount(lower);
        Span<byte> srcBytes = byteCount <= 512
            ? stackalloc byte[byteCount]
            : new byte[byteCount]; // very long paths fall back to heap
        Encoding.UTF8.GetBytes(lower, srcBytes);
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(srcBytes, hash);
        return Path.Combine(CacheDir, Convert.ToHexString(hash) + ".jpg");
    }

    /// <summary>
    /// Checks if a thumbnail exists for <paramref name="imagePath"/>.
    /// After <see cref="PreloadCacheIndex"/> has run, this is an O(1) in-memory lookup
    /// with no disk I/O — safe to call on the UI thread in a hot scroll path.
    /// </summary>
    public static bool TryGetCachedPath(string imagePath, out string thumbPath)
    {
        thumbPath = GetCachePath(imagePath);
        if (_knownCached.ContainsKey(thumbPath)) return true;
        // Fallback for files written after startup (e.g. scan happened mid-session).
        if (File.Exists(thumbPath))
        {
            _knownCached.TryAdd(thumbPath, 0);
            return true;
        }
        return false;
    }

    // ── Generation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a thumbnail for <paramref name="imagePath"/> if one doesn't already exist.
    /// Uses <see cref="DecoderOptions.TargetSize"/> so JPEG decoders use DCT subsampling and
    /// never allocate the full pixel buffer for 8K+ source images (~130 MB for 7680×4320).
    /// </summary>
    public static async Task GenerateAsync(string imagePath)
    {
        var thumbPath = GetCachePath(imagePath);
        if (File.Exists(thumbPath)) return;

        try
        {
            Directory.CreateDirectory(CacheDir);

            // TargetSize tells the JPEG decoder to use hardware DCT scaling so it only
            // allocates memory proportional to the output size, not the full source image.
            // For non-JPEG formats (PNG, WebP, BMP) the TargetSize hint is ignored and the
            // full image is decoded. Loading as Rgb24 instead of the default Rgba32 reduces
            // peak memory by ~25% (3 bytes/px vs 4) — e.g. ~345 MB vs ~460 MB for 12K PNG.
            var opts = new DecoderOptions
            {
                TargetSize = new Size(MaxThumbDimension, MaxThumbDimension),
            };
            using var image = await Image.LoadAsync<Rgb24>(opts, imagePath);

            if (image.Width > MaxThumbDimension || image.Height > MaxThumbDimension)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(MaxThumbDimension, MaxThumbDimension),
                    Mode = ResizeMode.Max,
                }));
            }

            await image.SaveAsJpegAsync(thumbPath, new JpegEncoder { Quality = 80 });
            _knownCached.TryAdd(thumbPath, 0);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Thumbnail generation failed for '{imagePath}': {ex.Message}");
            // Fall back to loading from the original — gallery will still work.
        }
    }

    /// <summary>
    /// Generates thumbnails for a batch of image paths in parallel (max 4 concurrent).
    /// </summary>
    public static Task GenerateBatchAsync(IEnumerable<string> imagePaths) =>
        Parallel.ForEachAsync(
            imagePaths,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (path, _) => await GenerateAsync(path));

    // ── Maintenance ───────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes cached thumbnails whose source images are no longer in the library.
    /// Called after a scan so stale entries don't accumulate indefinitely.
    /// </summary>
    public static Task CleanupStaleAsync(IEnumerable<string> activeImagePaths) =>
        Task.Run(() =>
        {
            if (!Directory.Exists(CacheDir)) return;

            var activeHashes = activeImagePaths
                .Select(p => Path.GetFileName(GetCachePath(p)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(CacheDir, "*.jpg"))
            {
                if (!activeHashes.Contains(Path.GetFileName(file)))
                {
                    try { File.Delete(file); _knownCached.TryRemove(file, out _); } catch { /* ignore locked files */ }
                }
            }
        });
}
