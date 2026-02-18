using System.Globalization;
using Microsoft.Maui.Graphics;
using MarketScanner.ViewModels;

namespace MarketScanner.Converters;

/// <summary>
/// Returns a row background color based on selection and dropped state.
/// </summary>
public sealed class QuoteRowBackgroundConverter : IMultiValueConverter
{
    private static readonly Color NormalColor = Color.FromArgb("#1a1a1a");
    private static readonly Color DroppedColor = Color.FromArgb("#0f0f0f");
    private static readonly Color SelectedColor = Color.FromArgb("#2a2a2a");

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not ScannerRowViewModel row)
        {
            return NormalColor;
        }

        var selected = values[1] as ScannerRowViewModel;
        var isDropped = values[2] is bool dropped && dropped;

        if (ReferenceEquals(row, selected))
        {
            return SelectedColor;
        }

        return isDropped ? DroppedColor : NormalColor;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
