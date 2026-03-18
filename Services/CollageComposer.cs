using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using BackgroundSlideShow.Models;

namespace BackgroundSlideShow.Services;

public enum CollageLayout
{
    ThreeLeft,      // [Big | A / B]         — 1 large left, 2 stacked right
    ThreeRight,     // [A / B | Big]         — 2 stacked left, 1 large right
    FourGrid,       // [A | B / C | D]       — 2×2 grid
    FiveThreeTwo,   // [A | B | C / D | E]   — 3 across top, 2 across bottom
    FiveTwoThree,   // [A | B / C | D | E]   — 2 across top, 3 across bottom
    SixGrid,        // [A | B | C / D | E | F] — 3×2 grid

    // Portrait-optimized layouts — tall vertical columns ideal for portrait images
    TwoColumns,     // [A | B]               — 2 equal full-height columns
    ThreeColumns,   // [A | B | C]           — 3 equal full-height columns
    FourColumns,    // [A | B | C | D]       — 4 equal full-height columns
}

/// <summary>
/// Composites multiple images into a single JPEG using one of the Windows
/// lock-screen collage layouts. Each panel is cover-cropped to fill its cell.
/// </summary>
internal static class CollageComposer
{
    /// <summary>Pixel width of the dark gap between panels (matches Windows).</summary>
    private const int GapPx = 2;

    /// <summary>Returns how many source images the given layout requires.</summary>
    public static int ImagesNeeded(CollageLayout layout) => layout switch
    {
        CollageLayout.ThreeLeft  or CollageLayout.ThreeRight   => 3,
        CollageLayout.FourGrid                                  => 4,
        CollageLayout.FiveThreeTwo or CollageLayout.FiveTwoThree => 5,
        CollageLayout.SixGrid                                   => 6,
        CollageLayout.TwoColumns                                => 2,
        CollageLayout.ThreeColumns                              => 3,
        CollageLayout.FourColumns                               => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };

    /// <summary>
    /// Picks how many images a collage should use via weighted random selection.
    /// Returns a value between 2 and 6 inclusive, limited by <paramref name="available"/>.
    /// Minimum of 2 images required for any collage.
    /// </summary>
    public static int PickImageCount(int available, Random rng)
    {
        var candidates = new List<(int Count, int Weight)>();

        if (available >= 2) candidates.Add((2, 1));
        if (available >= 3) candidates.Add((3, 3));
        if (available >= 4) candidates.Add((4, 2));
        if (available >= 5) candidates.Add((5, 2));
        if (available >= 6) candidates.Add((6, 1));

        if (candidates.Count == 0) return 0;

        int total = candidates.Sum(c => c.Weight);
        int pick  = rng.Next(total);
        int cum   = 0;
        foreach (var (count, weight) in candidates)
        {
            cum += weight;
            if (pick < cum) return count;
        }
        return candidates[0].Count;
    }

    /// <summary>
    /// Given a set of images and canvas dimensions, scores every layout that fits
    /// the image count and returns the one that minimises cover-crop waste.
    /// Images are reordered so each image lands in the cell whose aspect ratio
    /// best matches its own (via the rearrangement inequality on sorted ARs).
    /// Ties are broken randomly.
    /// </summary>
    public static (CollageLayout Layout, List<string> OrderedPaths) PickBestLayout(
        IReadOnlyList<ImageEntry> entries, int canvasW, int canvasH, Random rng)
    {
        int count = entries.Count;
        var matchingLayouts = Enum.GetValues<CollageLayout>()
            .Where(l => ImagesNeeded(l) == count)
            .ToList();

        if (matchingLayouts.Count == 0)
            throw new ArgumentException($"No layout supports {count} images");

        double bestScore = -1;
        var tied = new List<(CollageLayout Layout, List<string> Paths)>();

        foreach (var layout in matchingLayouts)
        {
            var cells  = GetCells(layout, canvasW, canvasH);
            var cellArs = cells.Select(c => (double)c.W / c.H).ToArray();

            // Optimal assignment via the rearrangement inequality: sort both
            // cell ARs and image ARs in the same order, then pair them.
            // This maximises the sum of AR-match scores across all pairings.
            var cellRanked = Enumerable.Range(0, count).OrderBy(i => cellArs[i]).ToArray();
            var imgRanked  = Enumerable.Range(0, count).OrderBy(i => entries[i].AspectRatio).ToArray();

            // assignment[cellIndex] = imageIndex
            var assignment = new int[count];
            for (int rank = 0; rank < count; rank++)
                assignment[cellRanked[rank]] = imgRanked[rank];

            // Score: sum of AR-match quality for each pairing.
            // min(a,b)/max(a,b) ∈ (0,1] where 1.0 = identical AR = zero crop waste.
            double score = 0;
            for (int ci = 0; ci < count; ci++)
            {
                double imgAr  = entries[assignment[ci]].AspectRatio;
                double cellAr = cellArs[ci];
                score += Math.Min(imgAr, cellAr) / Math.Max(imgAr, cellAr);
            }

            if (score > bestScore + 0.001)
            {
                bestScore = score;
                tied.Clear();
                tied.Add((layout, assignment.Select(ii => entries[ii].FilePath).ToList()));
            }
            else if (Math.Abs(score - bestScore) <= 0.001)
            {
                tied.Add((layout, assignment.Select(ii => entries[ii].FilePath).ToList()));
            }
        }

        return tied[rng.Next(tied.Count)];
    }

    /// <summary>
    /// Convenience overload of <see cref="PickBestLayout"/> for callers that only have
    /// file paths (e.g. the lock-screen engine). Reads image dimensions from headers
    /// via ImageSharp's <c>Identify</c> to compute aspect ratios, then delegates to
    /// the main scoring logic.
    /// </summary>
    public static (CollageLayout Layout, List<string> OrderedPaths) PickBestLayoutFromPaths(
        IReadOnlyList<string> imagePaths, int canvasW, int canvasH, Random rng)
    {
        // Build lightweight ImageEntry-like objects just for scoring.
        var entries = new List<ImageEntry>(imagePaths.Count);
        foreach (var path in imagePaths)
        {
            int w = 1920, h = 1080; // safe fallback
            try
            {
                var info = Image.Identify(path);
                if (info is not null) { w = info.Width; h = info.Height; }
            }
            catch { /* use fallback dimensions */ }

            entries.Add(new ImageEntry
            {
                FilePath = path,
                Width    = w,
                Height   = h,
            });
        }
        return PickBestLayout(entries, canvasW, canvasH, rng);
    }

    /// <summary>
    /// Composites <paramref name="imagePaths"/> into a single JPEG written to
    /// <paramref name="outputPath"/>. Each image is cover-cropped to its panel cell.
    /// </summary>
    public static void Compose(
        CollageLayout          layout,
        IReadOnlyList<string>  imagePaths,
        int                    canvasW,
        int                    canvasH,
        string                 outputPath)
    {
        var cells = GetCells(layout, canvasW, canvasH);

        using var canvas = new Image<Rgb24>(canvasW, canvasH, new Rgb24(0, 0, 0));

        for (int i = 0; i < cells.Count && i < imagePaths.Count; i++)
        {
            var (cx, cy, cw, ch) = cells[i];
            try
            {
                using var panel = LoadAsRgb24(imagePaths[i]);
                panel.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size     = new Size(cw, ch),
                    Mode     = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center,
                }));
                canvas.Mutate(x => x.DrawImage(panel, new Point(cx, cy), opacity: 1f));
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"CollageComposer: skipping '{imagePaths[i]}' — {ex.Message}");
            }
        }

        canvas.Save(outputPath, new JpegEncoder { Quality = 92 });
    }

    /// <summary>
    /// Composites a single image onto a canvas of the given size using the
    /// specified fit mode, then saves as JPEG to <paramref name="outputPath"/>.
    /// </summary>
    public static void ComposeSingle(
        string  imagePath,
        FitMode fitMode,
        int     canvasW,
        int     canvasH,
        string  outputPath)
    {
        using var src    = LoadAsRgb24(imagePath);
        using var canvas = new Image<Rgb24>(canvasW, canvasH, new Rgb24(0, 0, 0));

        switch (fitMode)
        {
            case FitMode.Fill:
            {
                // Cover-crop: scale so image fills the canvas, crop the excess.
                float scale = Math.Max((float)canvasW / src.Width, (float)canvasH / src.Height);
                int newW = (int)(src.Width * scale);
                int newH = (int)(src.Height * scale);
                src.Mutate(x => x.Resize(newW, newH));
                int ox = (canvasW - newW) / 2;
                int oy = (canvasH - newH) / 2;
                canvas.Mutate(x => x.DrawImage(src, new Point(ox, oy), 1f));
                break;
            }
            case FitMode.Fit:
            {
                // Letterbox/pillarbox: scale to fit within canvas, black bars around.
                float scale = Math.Min((float)canvasW / src.Width, (float)canvasH / src.Height);
                int newW = (int)(src.Width * scale);
                int newH = (int)(src.Height * scale);
                src.Mutate(x => x.Resize(newW, newH));
                int ox = (canvasW - newW) / 2;
                int oy = (canvasH - newH) / 2;
                canvas.Mutate(x => x.DrawImage(src, new Point(ox, oy), 1f));
                break;
            }
            case FitMode.Stretch:
            {
                // Distort to fill exact canvas dimensions.
                src.Mutate(x => x.Resize(canvasW, canvasH));
                canvas.Mutate(x => x.DrawImage(src, Point.Empty, 1f));
                break;
            }
            case FitMode.Center:
            {
                // Place at native resolution, centered; scale down only if larger than canvas.
                if (src.Width > canvasW || src.Height > canvasH)
                {
                    float scale = Math.Min((float)canvasW / src.Width, (float)canvasH / src.Height);
                    src.Mutate(x => x.Resize((int)(src.Width * scale), (int)(src.Height * scale)));
                }
                int ox = (canvasW - src.Width) / 2;
                int oy = (canvasH - src.Height) / 2;
                canvas.Mutate(x => x.DrawImage(src, new Point(ox, oy), 1f));
                break;
            }
            case FitMode.Tile:
            {
                for (int ty = 0; ty < canvasH; ty += src.Height)
                    for (int tx = 0; tx < canvasW; tx += src.Width)
                    {
                        var pt = new Point(tx, ty);
                        canvas.Mutate(x => x.DrawImage(src, pt, 1f));
                    }
                break;
            }
        }

        canvas.Save(outputPath, new JpegEncoder { Quality = 92 });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Image<Rgb24> LoadAsRgb24(string path)
    {
        if (WicHelper.IsHeic(path))
        {
            // Decode HEIC/HEIF via WIC to a temporary JPEG, then load with ImageSharp.
            string tmp = Path.Combine(Path.GetTempPath(), $"bss_heic_{Guid.NewGuid():N}.jpg");
            try
            {
                WicHelper.FitResizeSaveAsJpeg(path, 8192, tmp, quality: 95);
                return Image.Load<Rgb24>(tmp);
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* ignore cleanup failure */ }
            }
        }
        return Image.Load<Rgb24>(path);
    }

    /// <summary>
    /// Returns the (x, y, width, height) cell rectangles for each panel in the
    /// given layout, accounting for the dark gap between panels.
    /// </summary>
    private static List<(int X, int Y, int W, int H)> GetCells(CollageLayout layout, int W, int H)
    {
        int g       = GapPx;
        int halfW   = (W - g) / 2;
        int halfH   = (H - g) / 2;
        int bigW    = W * 2 / 3;
        int smW     = W - bigW - g;
        int thirdW  = (W - 2 * g) / 3;
        int fourthW = (W - 3 * g) / 4;

        return layout switch
        {
            CollageLayout.ThreeLeft =>
            [
                (0,        0,         bigW, H),
                (bigW + g, 0,         smW,  halfH),
                (bigW + g, halfH + g, smW,  H - halfH - g),
            ],

            CollageLayout.ThreeRight =>
            [
                (0,       0,         smW,  halfH),
                (0,       halfH + g, smW,  H - halfH - g),
                (smW + g, 0,         bigW, H),
            ],

            CollageLayout.FourGrid =>
            [
                (0,         0,         halfW,         halfH),
                (halfW + g, 0,         W - halfW - g, halfH),
                (0,         halfH + g, halfW,         H - halfH - g),
                (halfW + g, halfH + g, W - halfW - g, H - halfH - g),
            ],

            // 3 across top half, 2 across bottom half
            CollageLayout.FiveThreeTwo =>
            [
                (0,                0, thirdW,             halfH),
                (thirdW + g,       0, thirdW,             halfH),
                (2*(thirdW + g),   0, W - 2*(thirdW + g), halfH),
                (0,         halfH+g, halfW,               H - halfH - g),
                (halfW + g, halfH+g, W - halfW - g,       H - halfH - g),
            ],

            // 2 across top half, 3 across bottom half
            CollageLayout.FiveTwoThree =>
            [
                (0,               0, halfW,               halfH),
                (halfW + g,       0, W - halfW - g,       halfH),
                (0,         halfH+g, thirdW,               H - halfH - g),
                (thirdW + g,halfH+g, thirdW,               H - halfH - g),
                (2*(thirdW+g),halfH+g, W - 2*(thirdW+g),  H - halfH - g),
            ],

            // 3×2 grid
            CollageLayout.SixGrid =>
            [
                (0,              0, thirdW,             halfH),
                (thirdW + g,     0, thirdW,             halfH),
                (2*(thirdW + g), 0, W - 2*(thirdW + g), halfH),
                (0,              halfH+g, thirdW,             H - halfH - g),
                (thirdW + g,     halfH+g, thirdW,             H - halfH - g),
                (2*(thirdW + g), halfH+g, W - 2*(thirdW + g), H - halfH - g),
            ],

            // Portrait-optimized: full-height vertical columns
            CollageLayout.TwoColumns =>
            [
                (0,         0, halfW,         H),
                (halfW + g, 0, W - halfW - g, H),
            ],

            CollageLayout.ThreeColumns =>
            [
                (0,              0, thirdW,             H),
                (thirdW + g,     0, thirdW,             H),
                (2*(thirdW + g), 0, W - 2*(thirdW + g), H),
            ],

            CollageLayout.FourColumns =>
            [
                (0,                    0, fourthW,             H),
                (fourthW + g,          0, fourthW,             H),
                (2 * (fourthW + g),    0, fourthW,             H),
                (3 * (fourthW + g),    0, W - 3*(fourthW + g), H),
            ],

            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };
    }
}
