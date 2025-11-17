using System.Globalization;
using System.Collections;
using MarketScanner.Services;

namespace MarketScanner.Converters;

/// <summary>
/// Multi-value converter that checks if an item is selected by comparing its index in the collection.
/// Used for highlighting selected items in search results.
/// </summary>
public sealed class IsSelectedItemConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // values[0] = SymbolSearchResult item (BindingContext)
        // values[1] = ObservableCollection<SymbolSearchResult> (SearchResults)
        // values[2] = int (SelectedSearchResultIndex)
        if (values.Length >= 3 && 
            values[0] is SymbolSearchResult item &&
            values[1] is IList collection &&
            values[2] is int selectedIndex)
        {
            var currentIndex = collection.IndexOf(item);
            return currentIndex == selectedIndex;
        }
        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

