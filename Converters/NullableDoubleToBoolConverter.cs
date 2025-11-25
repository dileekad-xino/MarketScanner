using System.Globalization;

namespace MarketScanner.Converters;

public class NullableDoubleToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // When a nullable double is boxed, it becomes either a double (if it has a value) or null (if it doesn't)
        // So we just need to check if it's a double or null
        if (value is null)
            return false;
        
        // If it's a double (boxed from either double or double? with a value), return true
        return value is double;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

