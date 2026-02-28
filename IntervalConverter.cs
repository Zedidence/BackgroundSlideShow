using System.Globalization;
using System.Windows.Data;

namespace BackgroundSlideShow;

[ValueConversion(typeof(int), typeof(string))]
public class IntervalConverter : IValueConverter
{
    public static readonly IntervalConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int seconds) return "?";

        if (seconds < 60)
            return $"{seconds} sec";
        if (seconds < 3600)
        {
            int min = seconds / 60;
            int sec = seconds % 60;
            return sec == 0 ? $"{min} min" : $"{min} min {sec} sec";
        }

        int hours = seconds / 3600;
        int remaining = (seconds % 3600) / 60;
        return remaining == 0 ? $"{hours} hr" : $"{hours} hr {remaining} min";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
