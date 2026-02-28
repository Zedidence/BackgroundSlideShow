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
    public static IEnumerable<ImageEntry> FilterWithFallback(
        IEnumerable<ImageEntry> pool,
        ImagePoolMode mode,
        bool isPortraitMonitor)
    {
        var eligible = pool.ToList();
        return mode switch
        {
            ImagePoolMode.Landscape => Fallback(eligible, eligible.Where(i => i.IsLandscape).ToList()),
            ImagePoolMode.Portrait  => Fallback(eligible, eligible.Where(i => i.IsPortrait).ToList()),
            ImagePoolMode.Smart     => FallbackSized(eligible,
                                          isPortraitMonitor
                                              ? eligible.Where(i => i.IsPortrait).ToList()
                                              : eligible.Where(i => i.IsLandscape).ToList()),
            _                       => eligible,
        };

        static IEnumerable<ImageEntry> Fallback(List<ImageEntry> all, List<ImageEntry> preferred) =>
            preferred.Count > 0 ? preferred : all;

        static IEnumerable<ImageEntry> FallbackSized(List<ImageEntry> all, List<ImageEntry> preferred) =>
            preferred.Count >= MinPreferredPoolSize ? preferred : all;
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
