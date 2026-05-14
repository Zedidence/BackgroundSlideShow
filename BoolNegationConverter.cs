using System.Globalization;
using System.Windows.Data;

namespace BackgroundSlideShow;

/// <summary>Returns the logical negation of a bool binding value.</summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class BoolNegationConverter : IValueConverter
{
    public static readonly BoolNegationConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}
