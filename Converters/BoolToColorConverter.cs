using System.Globalization;
using Microsoft.Maui.Graphics;

namespace MarketScanner.Converters;

public class BoolToColorConverter : IValueConverter
{
    public Color TrueColor { get; set; } = Color.FromArgb("#0f0f0f");
    public Color FalseColor { get; set; } = Color.FromArgb("#1a1a1a");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
            return boolValue ? TrueColor : FalseColor;
        return FalseColor;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

