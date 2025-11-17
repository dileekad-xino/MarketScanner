using System.Globalization;
using System.Collections;
using Microsoft.Maui.Graphics;
using MarketScanner.Services;

namespace MarketScanner.Converters;

/// <summary>
/// Multi-value converter that returns a background color for selected items in search results.
/// </summary>
public sealed class SelectedItemBackgroundConverter : IMultiValueConverter
{
    // Selected item background color (SurfaceVariantDark equivalent: #2a2a2a)
    private static readonly Color SelectedColor = Color.FromArgb("#2a2a2a");
    // Unselected item background (Transparent)
    private static readonly Color UnselectedColor = Colors.Transparent;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // values[0] = SymbolSearchResult item (BindingContext)
        // values[1] = ObservableCollection<SymbolSearchResult> (SearchResults)
        // values[2] = int (SelectedSearchResultIndex)
        if (values.Length >= 3 && 
            values[0] is SymbolSearchResult item &&
            values[1] is IList collection &&
            values[2] is int selectedIndex && selectedIndex >= 0)
        {
            var currentIndex = collection.IndexOf(item);
            if (currentIndex == selectedIndex)
            {
                return SelectedColor;
            }
        }
        return UnselectedColor;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

