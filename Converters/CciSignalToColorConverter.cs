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
            "BULLISH MOMENTUM" => Color.FromArgb("#00c853"),
            "BULLISH ENTRY" => Color.FromArgb("#4caf50"),
            "OVERSOLD SCALP" => Color.FromArgb("#4caf50"),
            "BEARISH MOMENTUM" => Color.FromArgb("#d50000"),
            "TAKE PROFIT" => Color.FromArgb("#ff5252"),
            "EXIT WEAKNESS" => Color.FromArgb("#ff5252"),
            "NEUTRAL" => Color.FromArgb("#e0e0e0"),
            _ => Color.FromArgb("#e0e0e0")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

