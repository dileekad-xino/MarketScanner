using System.Globalization;
using MarketScanner.Services;

namespace MarketScanner.Converters;

/// <summary>
/// Converter that checks if an item is selected by comparing its index in the collection.
/// Used for highlighting selected items in search results.
/// </summary>
public sealed class IndexToIsSelectedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // value is the SymbolSearchResult item
        // parameter is a tuple or object containing (collection, selectedIndex)
        // For simplicity, we'll use a different approach - bind directly to SelectedSearchResultIndex
        // and use IndexOf in the converter
        if (value is SymbolSearchResult item && parameter is Tuple<System.Collections.IList, int> tuple)
        {
            var collection = tuple.Item1;
            var selectedIndex = tuple.Item2;
            var currentIndex = collection.IndexOf(item);
            return currentIndex == selectedIndex;
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

