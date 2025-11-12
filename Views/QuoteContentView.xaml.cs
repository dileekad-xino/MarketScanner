namespace MarketScanner.Views;

public partial class QuoteContentView : ContentView
{
    public QuoteContentView()
    {
        InitializeComponent();
    }

    private void OnSymbolTextChanged(object? sender, TextChangedEventArgs e)
    {
        // Update ViewModel property to trigger OnNewSymbolTextChanged
        System.Diagnostics.Debug.WriteLine($"QuoteContentView.OnSymbolTextChanged: NewTextValue='{e.NewTextValue}'");
        if (BindingContext is ViewModels.QuoteViewModel viewModel)
        {
            var oldValue = viewModel.NewSymbolText;
            viewModel.NewSymbolText = e.NewTextValue ?? "";
            System.Diagnostics.Debug.WriteLine($"QuoteContentView.OnSymbolTextChanged: Updated ViewModel property from '{oldValue}' to '{viewModel.NewSymbolText}'");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"QuoteContentView.OnSymbolTextChanged: BindingContext is not QuoteViewModel (type: {BindingContext?.GetType().Name ?? "null"})");
        }
    }

    private void OnSymbolEntryUnfocused(object? sender, FocusEventArgs e)
    {
        // Hide search results when entry loses focus
        if (BindingContext is ViewModels.QuoteViewModel viewModel)
        {
            viewModel.ShowSearchResults = false;
        }
    }
}

