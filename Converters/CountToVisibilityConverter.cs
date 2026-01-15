using System.Globalization;
using Microsoft.Maui.Controls;

namespace MarketScanner.Converters;

public class CountToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count && parameter is string paramStr && int.TryParse(paramStr, out int index))
        {
            // If index is -1, show when count is 0 (empty view)
            if (index == -1)
            {
                return count == 0;
            }
            // Otherwise, show if count > index (item exists at this index)
            return count > index;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
