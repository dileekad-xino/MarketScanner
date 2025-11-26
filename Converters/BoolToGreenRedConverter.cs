using System.Globalization;
using Microsoft.Maui.Graphics;

namespace MarketScanner.Converters;

public class BoolToGreenRedConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            // True = positive/green, False = negative/red
            return boolValue 
                ? Color.FromArgb("#4CAF50")  // PositiveGreen
                : Color.FromArgb("#EF5350"); // NegativeRed
        }
        return Color.FromArgb("#9CA3AF"); // Neutral gray
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

