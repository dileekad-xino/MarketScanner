using System.Globalization;

namespace MarketScanner.Converters;

public class CciSignalToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var signal = value as string;
        if (string.IsNullOrWhiteSpace(signal))
            return Color.FromArgb("#e0e0e0");

        signal = signal.ToUpperInvariant();
        return signal switch
        {
            "STRONG BUY" => Color.FromArgb("#00c853"),
            "BUY" => Color.FromArgb("#4caf50"),
            "STRONG SELL" => Color.FromArgb("#d50000"),
            "SELL" => Color.FromArgb("#ff5252"),
            _ => Color.FromArgb("#e0e0e0")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

