using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketScanner.Models;
using MarketScanner.Services;
using Microsoft.Extensions.Logging;

namespace MarketScanner.ViewModels;

public partial class DailyPlViewModel : ObservableObject
{
    private readonly ITradeService _tradeService;
    private readonly ILogger<DailyPlViewModel> _logger;
    private IDispatcherTimer? _autoRefreshTimer;

    [ObservableProperty] private ObservableCollection<TradeRowViewModel> _trades = new();
    [ObservableProperty] private ObservableCollection<TradeRowViewModel> _filteredTrades = new();
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private decimal _totalDailyPL;
    [ObservableProperty] private decimal _totalDailyPLPercent;
    [ObservableProperty] private DateTime _selectedDate = DateTime.Today;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isAutoRefreshEnabled;
    [ObservableProperty] private ObservableCollection<Models.RefreshIntervalOption> _refreshIntervals = new();
    [ObservableProperty] private Models.RefreshIntervalOption? _selectedRefreshInterval;

    public DailyPlViewModel(
        ITradeService tradeService,
        ILogger<DailyPlViewModel> logger)
    {
        _tradeService = tradeService;
        _logger = logger;

        RefreshIntervals = new ObservableCollection<Models.RefreshIntervalOption>
        {
            new Models.RefreshIntervalOption(5),
            new Models.RefreshIntervalOption(10),
            new Models.RefreshIntervalOption(15),
            new Models.RefreshIntervalOption(20),
            new Models.RefreshIntervalOption(25),
            new Models.RefreshIntervalOption(30)
        };
        SelectedRefreshInterval = RefreshIntervals.FirstOrDefault(i => i.Seconds == 10) ?? RefreshIntervals.FirstOrDefault();

        _autoRefreshTimer = Application.Current.Dispatcher.CreateTimer();
        _autoRefreshTimer.IsRepeating = true;
        _autoRefreshTimer.Tick += OnAutoRefreshTick;
        UpdateAutoRefreshTimer();
    }

    public async Task InitializeAsync()
    {
        await LoadTradesAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadTradesAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterTrades();
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        _ = LoadTradesAsync();
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        UpdateAutoRefreshTimer();
    }

    partial void OnSelectedRefreshIntervalChanged(Models.RefreshIntervalOption? value)
    {
        UpdateAutoRefreshTimer();
    }

    public async Task LoadTradesAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;

            var trades = await _tradeService.GetTradesByDateAsync(SelectedDate);
            
            Trades.Clear();
            foreach (var trade in trades)
            {
                Trades.Add(TradeRowViewModel.FromTrade(trade));
            }

            // Calculate aggregate P/L
            var (totalPL, totalPLPercent) = await _tradeService.GetDailyPLAsync(SelectedDate);
            TotalDailyPL = totalPL;
            TotalDailyPLPercent = totalPLPercent;

            // Apply current search filter
            FilterTrades();

            _logger.LogInformation("Loaded {Count} trades for {Date}", trades.Count, SelectedDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load trades");
            ErrorMessage = $"Failed to load trades: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void FilterTrades()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            FilteredTrades.Clear();
            foreach (var trade in Trades)
            {
                FilteredTrades.Add(trade);
            }
        }
        else
        {
            var searchLower = SearchText.ToLowerInvariant();
            FilteredTrades.Clear();
            foreach (var trade in Trades)
            {
                if (trade.Symbol.ToLowerInvariant().Contains(searchLower))
                {
                    FilteredTrades.Add(trade);
                }
            }
        }

        // Recalculate aggregate P/L from filtered trades
        CalculateAggregatePL();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    private void CalculateAggregatePL()
    {
        if (FilteredTrades.Count == 0)
        {
            TotalDailyPL = 0;
            TotalDailyPLPercent = 0;
            return;
        }

        TotalDailyPL = FilteredTrades.Sum(t => t.ProfitLoss);
        
        var totalEntryValue = FilteredTrades.Sum(t => t.EntryPrice * t.Quantity);
        TotalDailyPLPercent = totalEntryValue > 0 
            ? (TotalDailyPL / totalEntryValue) * 100 
            : 0;
    }

    private void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (!IsAutoRefreshEnabled || IsLoading)
        {
            return;
        }

        _ = LoadTradesAsync();
    }

    private void UpdateAutoRefreshTimer()
    {
        if (_autoRefreshTimer == null)
        {
            return;
        }

        if (!IsAutoRefreshEnabled || SelectedRefreshInterval == null)
        {
            _autoRefreshTimer.Stop();
            return;
        }

        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(SelectedRefreshInterval.Seconds);
        _autoRefreshTimer.Start();
    }
}

