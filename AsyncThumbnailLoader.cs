using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using BackgroundSlideShow.Services;

namespace BackgroundSlideShow;

/// <summary>
/// Attached property that loads thumbnail bitmaps on a background thread so the UI thread
/// never blocks on file I/O or JPEG decode during gallery scroll.
///
/// Usage in XAML:  local:AsyncThumbnailLoader.SourcePath="{Binding FilePath}"
///
/// Flow:
///   1. Path set → ThumbnailCacheService.TryGetCachedPath (O(1) dict lookup after preload).
///   2. In-memory bitmap cache hit → assign Image.Source immediately on UI thread (free).
///   3. Cache miss → set Source=null, Task.Run decode + freeze, BeginInvoke to set Source.
/// </summary>
public static class AsyncThumbnailLoader
{
    // ── In-memory bitmap cache ────────────────────────────────────────────────
    // Bounded LRU keyed by load path (disk-cache thumb path, or original on cache miss).
    // Replaces the prior "drop everything when full" strategy, which produced visible
    // GC pauses by freeing ~96 MB of frozen bitmaps in one go on every overflow.

    private const int MaxCacheEntries = 600;

    private static readonly LinkedList<(string Key, BitmapImage Bitmap)> _lruList = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Bitmap)>> _lruIndex =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lruLock = new();

    internal static void ClearMemoryCache()
    {
        lock (_lruLock)
        {
            _lruList.Clear();
            _lruIndex.Clear();
        }
    }

    private static bool TryGetCached(string key, out BitmapImage bmp)
    {
        lock (_lruLock)
        {
            if (_lruIndex.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                bmp = node.Value.Bitmap;
                return true;
            }
        }
        bmp = null!;
        return false;
    }

    private static void CacheSet(string key, BitmapImage bmp)
    {
        lock (_lruLock)
        {
            if (_lruIndex.TryGetValue(key, out var existing))
            {
                _lruList.Remove(existing);
                existing.Value = (key, bmp);
                _lruList.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<(string, BitmapImage)>((key, bmp));
            _lruList.AddFirst(node);
            _lruIndex[key] = node;

            if (_lruList.Count > MaxCacheEntries)
            {
                var last = _lruList.Last!;
                _lruList.RemoveLast();
                _lruIndex.Remove(last.Value.Key);
            }
        }
    }

    // ── Attached property: SourcePath ─────────────────────────────────────────

    public static readonly DependencyProperty SourcePathProperty =
        DependencyProperty.RegisterAttached(
            "SourcePath",
            typeof(string),
            typeof(AsyncThumbnailLoader),
            new PropertyMetadata(null, OnSourcePathChanged));

    public static void SetSourcePath(Image element, string? value)
        => element.SetValue(SourcePathProperty, value);

    public static string? GetSourcePath(Image element)
        => (string?)element.GetValue(SourcePathProperty);

    // Per-Image CancellationTokenSource — cancels any in-flight decode when the
    // path changes (container recycled to a different item before Task.Run finishes).
    private static readonly DependencyProperty CancelProperty =
        DependencyProperty.RegisterAttached(
            "AsyncThumbnailCancel",
            typeof(CancellationTokenSource),
            typeof(AsyncThumbnailLoader),
            new PropertyMetadata(null));

    // ── Core handler ───────────────────────────────────────────────────────────

    private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;

        // Cancel any in-flight load for this element.
        if (img.GetValue(CancelProperty) is CancellationTokenSource old)
        {
            old.Cancel();
            old.Dispose();
            img.SetValue(CancelProperty, null);
        }

        var path = e.NewValue as string;
        if (string.IsNullOrEmpty(path))
        {
            img.Source = null;
            return;
        }

        // Resolve the load path. TryGetCachedPath is O(1) after PreloadCacheIndex() —
        // safe to call on the UI thread with no disk I/O.
        bool cacheHit = ThumbnailCacheService.TryGetCachedPath(path, out var loadPath);

        // ── Fast path: bitmap already decoded and in memory ──────────────────
        if (TryGetCached(loadPath, out var cached))
        {
            img.Source = cached;
            if (!cacheHit)
                _ = ThumbnailCacheService.GenerateAsync(path); // build cache for next time
            return;
        }

        // ── Slow path: decode off the UI thread ───────────────────────────────
        img.Source = null;  // clear stale image while new one loads
        var cts = new CancellationTokenSource();
        img.SetValue(CancelProperty, cts);
        var token      = cts.Token;
        var dispatcher = img.Dispatcher;

        _ = Task.Run(async () =>
        {
            if (token.IsCancellationRequested) return;

            // Choose the best source path to decode:
            //   Cached thumbnail  → load the 200px JPEG directly (always fast/small).
            //   Uncached JPEG     → load original; WPF's JPEG codec uses DCT subsampling
            //                       so DecodePixelWidth=200 keeps memory minimal.
            //   Uncached non-JPEG → PNG/WebP/BMP don't benefit from DecodePixelWidth;
            //                       WPF would decode the full ~345–460 MB source image.
            //                       Generate the 200px JPEG cache first, then load that.
            string resolvedPath;
            if (cacheHit)
            {
                resolvedPath = loadPath;
            }
            else
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg")
                {
                    resolvedPath = path;  // DCT subsampling makes this efficient
                    _ = ThumbnailCacheService.GenerateAsync(path); // cache for next open
                }
                else
                {
                    // Generate the disk cache first; once done loadPath is a small JPEG.
                    await ThumbnailCacheService.GenerateAsync(path);
                    resolvedPath = System.IO.File.Exists(loadPath) ? loadPath : path;
                }
            }

            if (token.IsCancellationRequested) return;

            BitmapImage? bmp = null;
            try
            {
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource        = new Uri(resolvedPath);
                bmp.CacheOption      = BitmapCacheOption.OnLoad;     // full decode on this thread
                bmp.DecodePixelWidth = ThumbnailCacheService.MaxThumbDimension; // cap to 200 px
                bmp.EndInit();
                bmp.Freeze(); // makes BitmapImage safe to share with the UI thread
            }
            catch
            {
                bmp = null;
            }

            // Skip caching if the load was cancelled — the user has scrolled past this
            // cell and the bitmap may belong to a stale path. Caching it would pollute
            // the LRU with images we'll never display.
            if (token.IsCancellationRequested) return;
            if (bmp != null) CacheSet(loadPath, bmp);

            // BeginInvoke schedules the work and returns a DispatcherOperation — we don't
            // need to await it, the discard makes that explicit and silences CS4014.
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                () =>
                {
                    if (!token.IsCancellationRequested)
                        img.Source = bmp;
                    // Dispose CTS now that the load is complete; prevents orphaned CTS
                    // on containers that are discarded rather than recycled to a new path.
                    if (img.GetValue(CancelProperty) is CancellationTokenSource cur
                        && ReferenceEquals(cur, cts))
                    {
                        img.SetValue(CancelProperty, null);
                        cts.Dispose();
                    }
                });
        }, token);
    }
}
