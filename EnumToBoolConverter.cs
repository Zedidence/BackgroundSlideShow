using System.Globalization;
using System.Windows.Data;

namespace BackgroundSlideShow;

/// <summary>
/// Converts an enum value to bool for use with RadioButton.IsChecked.
/// Usage: IsChecked="{Binding Config.Order, Converter={StaticResource EnumBoolConverter},
///                           ConverterParameter=Random}"
/// </summary>
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is string paramStr && value is Enum enumVal)
            return enumVal.ToString().Equals(paramStr, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string paramStr)
            return Enum.Parse(targetType, paramStr, ignoreCase: true);
        return Binding.DoNothing;
    }
}
