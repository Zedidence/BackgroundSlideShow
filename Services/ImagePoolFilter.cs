using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Shared image pool filtering logic used by both <see cref="ImageSelectorService"/>
/// (with fallback, for slideshow selection) and the gallery view (exact, for display).
/// </summary>
public static class ImagePoolFilter
{
    // Smart mode tuning constants
    private const int    AbsoluteMinPoolSize    = 5;
    private const int    MinPoolFractionDivisor = 20;  // 5% of library
    private const double AspectRatioTolerance   = 0.5; // ±50% of monitor AR
    private const int    MinShortSidePx         = 400; // minimum quality threshold

    /// <summary>
    /// Filters <paramref name="pool"/> by <paramref name="mode"/>, falling back to broader
    /// subsets when each preferred subset is too small. Used by the slideshow engine.
    /// <para>
    /// Smart mode applies three-stage filtering:
    /// 1. Images within ±50% of the monitor's aspect ratio (excludes near-square images).
    /// 2. Same-orientation images (strict: squares excluded).
    /// 3. Full pool fallback.
    /// Stages 1 and 2 first drop images below <see cref="MinShortSidePx"/> on their shorter
    /// side; if that quality filter leaves too few images, the unfiltered pool is used instead.
    /// </para>
    /// </summary>
    /// <param name="monitorAspectRatio">
    /// Width / Height of the target monitor. Values &lt; 1 indicate portrait orientation.
    /// </param>
    public static List<ImageEntry> FilterWithFallback(
        IEnumerable<ImageEntry> pool,
        ImagePoolMode mode,
        double monitorAspectRatio)
    {
        if (mode == ImagePoolMode.All)
            return pool.ToList();

        var all = pool.ToList();

        // Explicit modes: keep current simple orientation filter with full-pool fallback.
        // Squares (Width == Height) count as landscape per IsLandscape definition.
        if (mode != ImagePoolMode.Smart)
        {
            var oriented = mode == ImagePoolMode.Landscape
                ? all.Where(i => i.IsLandscape).ToList()
                : all.Where(i => i.IsPortrait).ToList();
            return oriented.Count > 0 ? oriented : all;
        }

        // ── Smart mode ────────────────────────────────────────────────────────
        bool isPortraitMonitor = monitorAspectRatio < 1.0;
        int  minSize           = Math.Max(AbsoluteMinPoolSize, all.Count / MinPoolFractionDivisor);

        // Quality gate: drop images whose shorter dimension is below threshold.
        // If too few survive, skip the quality gate rather than collapsing to all images early.
        var quality = all.Where(i => Math.Min(i.Width, i.Height) >= MinShortSidePx).ToList();
        var basePool = quality.Count >= minSize ? quality : all;

        // Stage 1: aspect-ratio match within ±50% of the monitor's AR.
        // A 16:9 monitor (AR ≈ 1.78) accepts images from AR ≈ 1.19 to AR ≈ 2.67,
        // so near-square images and ultra-wide outliers are naturally excluded.
        double lo      = monitorAspectRatio / (1.0 + AspectRatioTolerance);
        double hi      = monitorAspectRatio * (1.0 + AspectRatioTolerance);
        var    arMatch = basePool.Where(i => i.AspectRatio >= lo && i.AspectRatio <= hi).ToList();
        if (arMatch.Count >= minSize) return arMatch;

        // Stage 2: same orientation, strict inequality (Width > Height / Height > Width),
        // which excludes perfectly square images from either bucket.
        var orientMatch = basePool.Where(i => isPortraitMonitor
            ? i.Height > i.Width
            : i.Width  > i.Height).ToList();
        if (orientMatch.Count >= minSize) return orientMatch;

        // Stage 3: full (quality-gated) pool.
        return basePool;
    }

    /// <summary>
    /// Filters <paramref name="pool"/> by <paramref name="mode"/> with no fallback.
    /// Used by the gallery view to show the user exactly what each monitor would draw from.
    /// Smart mode uses strict orientation comparisons so square images are excluded from
    /// both landscape and portrait buckets, matching slideshow behaviour.
    /// </summary>
    public static IEnumerable<ImageEntry> FilterExact(
        IEnumerable<ImageEntry> pool,
        ImagePoolMode mode,
        bool isPortraitMonitor) =>
        mode switch
        {
            ImagePoolMode.Landscape => pool.Where(i => i.IsLandscape),
            ImagePoolMode.Portrait  => pool.Where(i => i.IsPortrait),
            // Strict comparison mirrors Smart fallback stage 2 (no squares)
            ImagePoolMode.Smart     => pool.Where(i => isPortraitMonitor
                                           ? i.Height > i.Width
                                           : i.Width  > i.Height),
            _                       => pool,
        };
}
