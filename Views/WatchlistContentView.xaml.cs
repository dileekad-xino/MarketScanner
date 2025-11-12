using Microsoft.Extensions.Logging;
using MarketScanner.ViewModels;

namespace MarketScanner.Views;

public partial class WatchlistContentView : ContentView
{
    public WatchlistContentView()
    {
        try
        {
            Console.WriteLine("WatchlistContentView: InitializeComponent() starting");
            InitializeComponent();
            Console.WriteLine("WatchlistContentView: InitializeComponent() completed successfully");
            
            // Auto-focus entry when popup becomes visible
            this.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BindingContext))
                {
                    if (BindingContext is WatchlistViewModel vm)
                    {
                        vm.PropertyChanged += (sender, args) =>
                        {
                            if (args.PropertyName == nameof(WatchlistViewModel.IsCreatingWatchlist) 
                                && vm.IsCreatingWatchlist)
                            {
                                Dispatcher.Dispatch(() =>
                                {
                                    WatchlistNameEntry?.Focus();
                                    if (WatchlistNameEntry != null)
                                    {
                                        WatchlistNameEntry.CursorPosition = 0;
                                        WatchlistNameEntry.SelectionLength = WatchlistNameEntry.Text?.Length ?? 0;
                                    }
                                });
                            }
                            
                            if (args.PropertyName == nameof(WatchlistViewModel.IsRenamingWatchlist) 
                                && vm.IsRenamingWatchlist)
                            {
                                Dispatcher.Dispatch(() =>
                                {
                                    RenamingWatchlistNameEntry?.Focus();
                                    if (RenamingWatchlistNameEntry != null)
                                    {
                                        RenamingWatchlistNameEntry.CursorPosition = 0;
                                        RenamingWatchlistNameEntry.SelectionLength = RenamingWatchlistNameEntry.Text?.Length ?? 0;
                                    }
                                });
                            }
                        };
                    }
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WatchlistContentView: InitializeComponent() FAILED: {ex.Message}");
            Console.WriteLine($"StackTrace: {ex.StackTrace}");
            throw;
        }
    }

    private void OnSymbolTextChanged(object? sender, TextChangedEventArgs e)
    {
        // Update ViewModel property to trigger OnNewSymbolTextChanged
        System.Diagnostics.Debug.WriteLine($"WatchlistContentView.OnSymbolTextChanged: NewTextValue='{e.NewTextValue}'");
        if (BindingContext is WatchlistViewModel viewModel)
        {
            var oldValue = viewModel.NewSymbolText;
            viewModel.NewSymbolText = e.NewTextValue ?? "";
            System.Diagnostics.Debug.WriteLine($"WatchlistContentView.OnSymbolTextChanged: Updated ViewModel property from '{oldValue}' to '{viewModel.NewSymbolText}'");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"WatchlistContentView.OnSymbolTextChanged: BindingContext is not WatchlistViewModel (type: {BindingContext?.GetType().Name ?? "null"})");
        }
    }

    private void OnSymbolEntryUnfocused(object? sender, FocusEventArgs e)
    {
        // Hide search results when entry loses focus
        if (BindingContext is WatchlistViewModel viewModel)
        {
            viewModel.ShowSearchResults = false;
        }
    }
}

