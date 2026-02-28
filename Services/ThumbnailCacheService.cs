using System.IO;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
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

    // ── Path helpers ──────────────────────────────────────────────────────────

    public static string GetCachePath(string imagePath)
    {
        var hash = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(imagePath.ToLowerInvariant())));
        return Path.Combine(CacheDir, hash + ".jpg");
    }

    public static bool TryGetCachedPath(string imagePath, out string thumbPath)
    {
        thumbPath = GetCachePath(imagePath);
        return File.Exists(thumbPath);
    }

    // ── Generation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a thumbnail for <paramref name="imagePath"/> if one doesn't already exist.
    /// Errors are silently swallowed — callers fall back to loading from the original.
    /// </summary>
    public static async Task GenerateAsync(string imagePath)
    {
        var thumbPath = GetCachePath(imagePath);
        if (File.Exists(thumbPath)) return;

        try
        {
            Directory.CreateDirectory(CacheDir);

            using var image = await Image.LoadAsync(imagePath);

            if (image.Width > MaxThumbDimension || image.Height > MaxThumbDimension)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(MaxThumbDimension, MaxThumbDimension),
                    Mode = ResizeMode.Max,
                }));
            }

            await image.SaveAsJpegAsync(thumbPath, new JpegEncoder { Quality = 80 });
        }
        catch
        {
            // If generation fails (corrupted image, locked file, etc.) just skip.
            // The converter will fall back to loading from the original.
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
                .Select(p => Convert.ToHexString(
                    MD5.HashData(Encoding.UTF8.GetBytes(p.ToLowerInvariant()))) + ".jpg")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(CacheDir, "*.jpg"))
            {
                if (!activeHashes.Contains(Path.GetFileName(file)))
                {
                    try { File.Delete(file); } catch { /* ignore locked files */ }
                }
            }
        });
}
