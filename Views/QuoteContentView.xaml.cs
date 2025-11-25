using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
#if WINDOWS
using Microsoft.UI.Xaml.Input;
using Windows.System;
#endif

namespace MarketScanner.Views;

public partial class QuoteContentView : ContentView
{
    private ViewModels.QuoteViewModel? _viewModel;
    private bool _chartReady;
    private Task? _loadHtmlTask;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public QuoteContentView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        _ = EnsureChartHtmlAsync();
    }

    protected override void OnBindingContextChanged()
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnBindingContextChanged();

        _viewModel = BindingContext as ViewModels.QuoteViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _ = TryPushChartSnapshotAsync();
        }
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
        if (BindingContext is ViewModels.QuoteViewModel viewModel)
        {
            viewModel.NewSymbolText = e.NewTextValue ?? "";
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

    private async Task EnsureChartHtmlAsync()
    {
        if (_loadHtmlTask != null)
        {
            await _loadHtmlTask;
            return;
        }

        _loadHtmlTask = LoadChartHtmlAsync();
        await _loadHtmlTask;
    }

    private async Task LoadChartHtmlAsync()
    {
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("quotes_chart.html").ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var html = await reader.ReadToEndAsync().ConfigureAwait(false);

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                ChartWebView.Source = new HtmlWebViewSource
                {
                    Html = html
                };
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load chart HTML: {ex}");
        }
    }

    private void OnChartWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        _chartReady = true;
        _ = TryPushChartSnapshotAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.QuoteViewModel.ChartSnapshot))
        {
            _ = TryPushChartSnapshotAsync();
        }
    }

    private async Task TryPushChartSnapshotAsync()
    {
        if (!_chartReady || _viewModel?.ChartSnapshot == null)
        {
            return;
        }

        try
        {
            var payloadJson = JsonSerializer.Serialize(_viewModel.ChartSnapshot, _jsonOptions);
            var script = $"window.marketScannerChart && window.marketScannerChart.setData({payloadJson});";
            await MainThread.InvokeOnMainThreadAsync(() => ChartWebView.EvaluateJavaScriptAsync(script));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to push chart data: {ex}");
        }
    }
}

