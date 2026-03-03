using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using BackgroundSlideShow.Services;

namespace BackgroundSlideShow;

[ValueConversion(typeof(string), typeof(BitmapImage))]
public class ThumbnailConverter : IValueConverter
{
    public static readonly ThumbnailConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return null;
        int decodeWidth = parameter is string s && int.TryParse(s, out var w) ? w : 64;
        try
        {
            // Prefer the pre-generated 200 px cache JPEG — much faster than decoding the
            // full-resolution original (which can be tens of megabytes for a 4 K wallpaper).
            string loadPath = ThumbnailCacheService.TryGetCachedPath(path, out var thumb) ? thumb : path;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(loadPath);
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
