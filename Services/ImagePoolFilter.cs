using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Shared image pool filtering logic used by both <see cref="ImageSelectorService"/>
/// (with fallback, for slideshow selection) and the gallery view (exact, for display).
/// </summary>
public static class ImagePoolFilter
{
    private const int MinPreferredPoolSize = 5;

    /// <summary>
    /// Filters <paramref name="pool"/> by <paramref name="mode"/>, falling back to the
    /// full pool when the preferred subset is too small. Used by the slideshow engine.
    /// </summary>
    public static List<ImageEntry> FilterWithFallback(
        IEnumerable<ImageEntry> pool,
        ImagePoolMode mode,
        bool isPortraitMonitor)
    {
        if (mode == ImagePoolMode.All)
            return pool.ToList();

        var all = pool.ToList();
        var preferred = mode switch
        {
            ImagePoolMode.Landscape => all.Where(i => i.IsLandscape).ToList(),
            ImagePoolMode.Portrait  => all.Where(i => i.IsPortrait).ToList(),
            _                       => isPortraitMonitor
                                           ? all.Where(i => i.IsPortrait).ToList()
                                           : all.Where(i => i.IsLandscape).ToList(),
        };

        bool usePreferred = mode == ImagePoolMode.Smart
            ? preferred.Count >= MinPreferredPoolSize
            : preferred.Count > 0;
        return usePreferred ? preferred : all;
    }

    /// <summary>
    /// Filters <paramref name="pool"/> by <paramref name="mode"/> with no fallback.
    /// Used by the gallery view to show the user exactly what each monitor would draw from.
    /// </summary>
    public static IEnumerable<ImageEntry> FilterExact(
        IEnumerable<ImageEntry> pool,
        ImagePoolMode mode,
        bool isPortraitMonitor) =>
        mode switch
        {
            ImagePoolMode.Landscape => pool.Where(i => i.IsLandscape),
            ImagePoolMode.Portrait  => pool.Where(i => i.IsPortrait),
            ImagePoolMode.Smart     => pool.Where(i => isPortraitMonitor ? i.IsPortrait : i.IsLandscape),
            _                       => pool,
        };
}
