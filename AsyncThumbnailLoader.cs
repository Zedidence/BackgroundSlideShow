using System.Collections.Concurrent;
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
    // Key: resolved load path (disk-cache thumb path, or original on cache miss).
    // Max 600 entries; drop-all eviction to avoid LRU bookkeeping overhead.
    // With 200 px JPEG thumbnails (~160 KB decoded each) the worst case is ~96 MB.

    private const int MaxCacheEntries = 600;

    private static readonly ConcurrentDictionary<string, BitmapImage> _memCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void ClearMemoryCache() => _memCache.Clear();

    private static void CacheSet(string key, BitmapImage bmp)
    {
        if (_memCache.Count >= MaxCacheEntries)
            _memCache.Clear();   // simple drop-all; acceptable for a gallery cache
        _memCache[key] = bmp;
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
        if (_memCache.TryGetValue(loadPath, out var cached))
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

        _ = Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;

            BitmapImage? bmp = null;
            try
            {
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource        = new Uri(loadPath);
                bmp.CacheOption      = BitmapCacheOption.OnLoad;     // full decode on this thread
                bmp.DecodePixelWidth = ThumbnailCacheService.MaxThumbDimension; // cap to 200 px
                bmp.EndInit();
                bmp.Freeze(); // makes BitmapImage safe to share with the UI thread
            }
            catch
            {
                bmp = null;
            }

            if (token.IsCancellationRequested) return;
            if (bmp != null) CacheSet(loadPath, bmp);

            dispatcher.BeginInvoke(
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

        // Kick off disk-cache generation for paths not yet cached —
        // next gallery open will load from the fast JPEG thumbnail instead.
        if (!cacheHit)
            _ = ThumbnailCacheService.GenerateAsync(path);
    }
}
