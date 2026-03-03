using System.Runtime.InteropServices;
using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.Services;

public class ImageSelectorService
{
    /// <summary>
    /// Builds a fully shuffled deck of all eligible images for a monitor.
    /// Filters by exclusion, optional folder whitelist, and image pool mode,
    /// then applies a Fisher-Yates shuffle so every image plays once before any repeats.
    /// </summary>
    /// <param name="allowedFolderIds">
    /// When non-null, only images from these folders are eligible (used when
    /// <see cref="FolderAssignmentMode.Selected"/> is active on the monitor).
    /// Pass null to include images from all folders.
    /// </param>
    public List<ImageEntry> BuildShuffledDeck(
        IReadOnlyList<ImageEntry> allImages,
        MonitorInfo monitor,
        MonitorConfig config,
        IReadOnlySet<int>? allowedFolderIds = null)
    {
        IEnumerable<ImageEntry> pool = allImages.Where(i => !i.IsExcluded);

        // Folder whitelist (only applied when FolderAssignmentMode = Selected)
        if (allowedFolderIds is not null)
            pool = pool.Where(i => allowedFolderIds.Contains(i.LibraryFolderId));

        var deck = ImagePoolFilter.FilterWithFallback(pool, config.ImagePoolMode, monitor.IsPortrait);
        Random.Shared.Shuffle(CollectionsMarshal.AsSpan(deck));
        return deck;
    }
}
