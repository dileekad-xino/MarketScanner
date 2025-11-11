using MarketScanner.Models;
using System.Globalization;

namespace MarketScanner.Converters;

public class AlgoActionToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AlgoAction action)
        {
            return action switch
            {
                AlgoAction.Buy => Application.Current?.Resources["GainColor"] ?? Colors.Green,
                AlgoAction.Sell => Application.Current?.Resources["LossColor"] ?? Colors.Red,
                AlgoAction.Hold => Application.Current?.Resources["NeutralColor"] ?? Colors.Orange,
                _ => Application.Current?.Resources["NeutralColor"] ?? Colors.Gray
            };
        }
        return Application.Current?.Resources["NeutralColor"] ?? Colors.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

