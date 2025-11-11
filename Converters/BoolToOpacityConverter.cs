using System.Globalization;

namespace MarketScanner.Converters;

public class BoolToOpacityConverter : IValueConverter
{
    public double TrueValue { get; set; } = 0.5;
    public double FalseValue { get; set; } = 1.0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? TrueValue : FalseValue;
        return FalseValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

