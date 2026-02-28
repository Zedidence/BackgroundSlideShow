using System.Globalization;
using System.Windows.Data;

namespace BackgroundSlideShow;

/// <summary>
/// Converts a nullable DateTime (UTC) to a human-readable relative string such as
/// "just now", "5 min ago", "3 days ago", or "Never" for null values.
/// </summary>
[ValueConversion(typeof(DateTime?), typeof(string))]
public class RelativeTimeConverter : IValueConverter
{
    public static readonly RelativeTimeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return "Never";

        var ts = DateTime.UtcNow - dt.ToUniversalTime();
        if (ts.TotalSeconds < 60) return "just now";
        if (ts.TotalHours < 1) return $"{(int)ts.TotalMinutes} min ago";
        if (ts.TotalDays < 1) return $"{(int)ts.TotalHours} hr ago";
        if (ts.TotalDays < 2) return "1 day ago";
        if (ts.TotalDays < 30) return $"{(int)ts.TotalDays} days ago";
        if (ts.TotalDays < 60) return "1 month ago";
        return $"{(int)(ts.TotalDays / 30)} months ago";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
