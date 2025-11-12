#if WINDOWS
using Microsoft.UI.Xaml.Input;
using Windows.System;
#endif

namespace MarketScanner.Views;

public partial class QuoteContentView : ContentView
{
    public QuoteContentView()
    {
        InitializeComponent();
    }

#if WINDOWS
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        
        // Hook up keyboard events for Windows
        if (SymbolEntry?.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
        {
            textBox.KeyDown += OnSymbolEntryKeyDown;
        }
    }

    private void OnSymbolEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (BindingContext is ViewModels.QuoteViewModel viewModel)
        {
            if (!viewModel.ShowSearchResults || viewModel.SearchResults.Count == 0)
                return;

            switch (e.Key)
            {
                case VirtualKey.Up:
                    e.Handled = true;
                    viewModel.NavigateSearchResultsUpCommand.Execute(null);
                    break;
                case VirtualKey.Down:
                    e.Handled = true;
                    viewModel.NavigateSearchResultsDownCommand.Execute(null);
                    break;
                case VirtualKey.Enter:
                    e.Handled = true;
                    if (viewModel.SelectedSearchResultIndex >= 0 && viewModel.SelectedSearchResultIndex < viewModel.SearchResults.Count)
                    {
                        var selectedResult = viewModel.SearchResults[viewModel.SelectedSearchResultIndex];
                        viewModel.SelectSearchResultCommand.Execute(selectedResult);
                        // Trigger AddQuoteCommand to add the selected symbol
                        viewModel.AddQuoteCommand.Execute(null);
                    }
                    else
                    {
                        // Fall back to normal Enter behavior (AddQuoteCommand)
                        viewModel.AddQuoteCommand.Execute(null);
                    }
                    break;
            }
        }
    }
#endif

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

