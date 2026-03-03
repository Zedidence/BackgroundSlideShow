using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using BackgroundSlideShow.Services;

namespace BackgroundSlideShow;

/// <summary>
/// Multi-value converter that produces a thumbnail BitmapImage from (filePath, decodeWidth).
/// Checks the disk thumbnail cache first for fast loading; falls back to decoding the
/// original with DecodePixelWidth and schedules async cache generation for next time.
/// </summary>
public class ThumbnailMultiConverter : IMultiValueConverter
{
    public static readonly ThumbnailMultiConverter Instance = new();

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 1 || values[0] is not string path || string.IsNullOrEmpty(path))
            return null;

        int decodeWidth = values.Length > 1 && values[1] is int size && size > 0 ? size : 64;

        try
        {
            // Prefer the pre-generated thumbnail — loading a 200 px JPEG is ~10× faster than
            // decoding a multi-megapixel original, even with DecodePixelWidth set.
            bool cacheHit = ThumbnailCacheService.TryGetCachedPath(path, out var thumbPath);
            // Fall back to the original file on cache miss so images always display.
            string loadPath = cacheHit ? thumbPath : path;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(loadPath);
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            // Cache miss: schedule async generation so the next gallery open is fast.
            if (!cacheHit)
                _ = ThumbnailCacheService.GenerateAsync(path);

            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
