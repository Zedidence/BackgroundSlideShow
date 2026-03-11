using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BackgroundSlideShow.Services;

/// <summary>
/// WIC-based image operations for formats not supported by SixLabors.ImageSharp, such as HEIC/HEIF.
/// Uses WPF's BitmapDecoder which delegates to the Windows Imaging Component (WIC). HEIC decoding
/// requires the Windows HEVC codec to be installed (available via the Microsoft Store on Windows 10/11).
/// All operations that touch BitmapDecoder/BitmapEncoder are marshalled to a shared long-lived STA
/// dispatcher thread since WIC COM objects require STA apartment state.
/// </summary>
internal static class WicHelper
{
    private static readonly HashSet<string> HeicExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    /// <summary>Returns true if <paramref name="path"/> is a HEIC or HEIF file.</summary>
    public static bool IsHeic(string path) =>
        HeicExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Returns the pixel dimensions of an image file using WIC.
    /// Uses <c>BitmapCreateOptions.DelayCreation</c> so only the frame header is read —
    /// no pixel data is allocated.
    /// </summary>
    public static (int Width, int Height) GetDimensions(string path) =>
        RunOnSta(() =>
        {
            var decoder = BitmapDecoder.Create(
                new Uri(path),
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        });

    /// <summary>
    /// Decodes an image using WIC, scales it to exactly <paramref name="targetW"/>×<paramref name="targetH"/>,
    /// and saves the result as a JPEG to <paramref name="destPath"/>.
    /// </summary>
    public static void DecodeResizeSaveAsJpeg(
        string sourcePath, int targetW, int targetH, string destPath, int quality = 95) =>
        RunOnSta(() =>
        {
            var decoder = BitmapDecoder.Create(
                new Uri(sourcePath),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();

            double scaleX = (double)targetW / frame.PixelWidth;
            double scaleY = (double)targetH / frame.PixelHeight;
            var scaled = new TransformedBitmap(frame, new ScaleTransform(scaleX, scaleY));
            scaled.Freeze();

            EncodeAsJpeg(scaled, destPath, quality);
        });

    /// <summary>
    /// Decodes an image using WIC, uniformly scales it to fit within a
    /// <paramref name="maxDimension"/>×<paramref name="maxDimension"/> bounding box (preserving
    /// aspect ratio), and saves the result as a JPEG to <paramref name="destPath"/>.
    /// Used by <see cref="ThumbnailCacheService"/> for HEIC thumbnails.
    /// </summary>
    public static void FitResizeSaveAsJpeg(
        string sourcePath, int maxDimension, string destPath, int quality = 80) =>
        RunOnSta(() =>
        {
            var decoder = BitmapDecoder.Create(
                new Uri(sourcePath),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();

            int w = frame.PixelWidth;
            int h = frame.PixelHeight;
            double scale = Math.Min((double)maxDimension / w, (double)maxDimension / h);
            // Only resize if larger than the target; if already smaller just encode as-is.
            BitmapSource source = frame;
            if (scale < 1.0)
            {
                var scaled = new TransformedBitmap(
                    frame, new ScaleTransform(scale, scale));
                scaled.Freeze();
                source = scaled;
            }

            EncodeAsJpeg(source, destPath, quality);
        });

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void EncodeAsJpeg(BitmapSource source, string destPath, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.OpenWrite(destPath);
        encoder.Save(stream);
    }

    // Single long-lived STA thread with a running Dispatcher message pump.
    // Re-using one thread instead of spawning a new one per WIC call eliminates
    // repeated thread create/start/join overhead — measurable when scanning
    // HEIC-heavy libraries where GetDimensions is called thousands of times.
    private static readonly Lazy<Dispatcher> _staDispatcher = new(() =>
    {
        Dispatcher? d = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            d = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run(); // blocks here until the dispatcher is shut down
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true; // will be killed automatically on app exit
        thread.Start();
        ready.Wait();
        return d!;
    });

    /// <summary>
    /// Runs <paramref name="func"/> on the shared STA dispatcher thread if the calling
    /// thread is not already STA. WIC COM objects (BitmapDecoder, BitmapEncoder) require
    /// STA apartment state. Timer/ThreadPool threads are MTA, so slideshow and thumbnail
    /// tasks need this wrapper.
    /// </summary>
    private static T RunOnSta<T>(Func<T> func)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return func();

        return _staDispatcher.Value.Invoke(func);
    }

    private static void RunOnSta(Action action) =>
        RunOnSta<int>(() => { action(); return 0; });
}
