using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using Microsoft.Extensions.Logging;

namespace MarketScanner.ViewModels;

public partial class AlgoRunnerViewModel : ObservableObject
{
    private readonly IAlgoStrategy _algorithm;
    private readonly ILogger<AlgoRunnerViewModel> _logger;
    private readonly ICandlestickBuilder? _candlestickBuilder;
    private readonly IbkrGatewayService? _ibkrGatewayService;
    private CancellationTokenSource? _cancellationTokenSource;
    private IDisposable? _tickSubscription;

    [ObservableProperty] private ScannerRowViewModel? _selectedSymbol;
    [ObservableProperty] private AlgoResult? _result;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _errorMessage = string.Empty;
    
    // Quantity and P/L tracking
    [ObservableProperty] private int _quantity = 100;
    [ObservableProperty] private decimal? _entryPrice;
    [ObservableProperty] private decimal? _exitPrice;
    [ObservableProperty] private decimal _positionValue;
    [ObservableProperty] private decimal _profitLoss;
    [ObservableProperty] private decimal _profitLossPercent;
    [ObservableProperty] private bool _hasPosition;
    [ObservableProperty] private bool _positionClosed;
    [ObservableProperty] private string _plCalculation = string.Empty;

    public AlgoRunnerViewModel(
        IAlgoStrategy algorithm,
        ILogger<AlgoRunnerViewModel> logger,
        ICandlestickBuilder? candlestickBuilder = null,
        IbkrGatewayService? ibkrGatewayService = null)
    {
        _algorithm = algorithm;
        _logger = logger;
        _candlestickBuilder = candlestickBuilder;
        _ibkrGatewayService = ibkrGatewayService;
    }

    public async Task InitializeAsync(ScannerRowViewModel symbol)
    {
        SelectedSymbol = symbol;
        ErrorMessage = string.Empty;
        Result = null;

        // Subscribe to live tick updates for this symbol
        SubscribeToTickUpdates();

        _logger.LogInformation("AlgoRunner initialized for symbol {Symbol} with algorithm {AlgorithmName}", 
            symbol.Symbol, _algorithm.Name);
    }

    private void SubscribeToTickUpdates()
    {
        if (_ibkrGatewayService == null || SelectedSymbol == null)
            return;

        _tickSubscription?.Dispose();
        _tickSubscription = _ibkrGatewayService.TickStream
            .Where(tick => tick.Symbol == SelectedSymbol.Symbol)
            .Subscribe(OnTickReceived);

        _logger.LogInformation("Subscribed to live tick updates for {Symbol}", SelectedSymbol.Symbol);
    }

    private void OnTickReceived(TickData tick)
    {
        if (SelectedSymbol == null || tick.Symbol != SelectedSymbol.Symbol)
            return;

        // Update the symbol's price data (this will trigger change % recalculation)
        if (tick.LastPrice.HasValue && tick.LastPrice.Value > 0)
        {
            SelectedSymbol.LastPrice = tick.LastPrice.Value;
        }

        // Update P/L if we have a position
        if (HasPosition && !PositionClosed)
        {
            UpdateProfitLossFromTick();
        }
    }

    private void UpdateProfitLossFromTick()
    {
        if (SelectedSymbol == null || !HasPosition || !EntryPrice.HasValue)
            return;

        var currentPrice = (decimal)SelectedSymbol.LastPrice;
        ExitPrice = currentPrice;
        PositionValue = currentPrice * Quantity;
        ProfitLoss = (currentPrice - EntryPrice.Value) * Quantity;
        ProfitLossPercent = EntryPrice.Value > 0 
            ? ((currentPrice - EntryPrice.Value) / EntryPrice.Value) * 100 
            : 0;
        PlCalculation = $"({currentPrice:C2} - {EntryPrice.Value:C2}) × {Quantity} = {ProfitLoss:C2}";
    }

    [RelayCommand]
    private async Task RunAlgoAsync()
    {
        if (SelectedSymbol == null)
        {
            ErrorMessage = "Please select a symbol";
            return;
        }

        try
        {
            IsRunning = true;
            ErrorMessage = string.Empty;
            Result = null;

            // Cancel any previous execution
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("Running algorithm {AlgorithmName} on symbol {Symbol}", 
                _algorithm.Name, SelectedSymbol.Symbol);

            // Execute algorithm
            Result = await _algorithm.ExecuteAsync(SelectedSymbol, _cancellationTokenSource.Token);

            _logger.LogInformation("Algorithm completed: {Action} for {Symbol} at {Price}", 
                Result.Action, Result.Symbol, Result.Price);

            // Update P/L calculations
            UpdateProfitLoss();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Algorithm execution was cancelled");
            ErrorMessage = "Algorithm execution was cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing algorithm");
            ErrorMessage = $"Error executing algorithm: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void CancelExecution()
    {
        _cancellationTokenSource?.Cancel();
        _logger.LogInformation("Algorithm execution cancelled by user");
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        // Cancel any running algorithm
        _cancellationTokenSource?.Cancel();
        
        // Unsubscribe from tick stream
        _tickSubscription?.Dispose();
        _tickSubscription = null;
        
        // Unsubscribe from candlestick builder when closing to free up resources
        UnsubscribeFromCandlestickBuilder();
        
        // Close the page
        if (Application.Current?.MainPage != null)
        {
            await Application.Current.MainPage.Navigation.PopModalAsync();
        }
    }

    private void UnsubscribeFromCandlestickBuilder()
    {
        if (_candlestickBuilder != null && SelectedSymbol != null)
        {
            try
            {
                _candlestickBuilder.UnsubscribeSymbol(SelectedSymbol.Symbol);
                _logger.LogInformation("Unsubscribed {Symbol} from candlestick builder", SelectedSymbol.Symbol);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unsubscribe {Symbol} from candlestick builder", SelectedSymbol.Symbol);
            }
        }
    }

    partial void OnQuantityChanged(int value)
    {
        UpdateProfitLoss();
    }

    private void UpdateProfitLoss()
    {
        if (SelectedSymbol == null || Result == null)
            return;

        var currentPrice = (decimal)SelectedSymbol.LastPrice;
        
        // First run - establish entry position at current price
        if (!HasPosition)
        {
            EntryPrice = currentPrice;
            ExitPrice = null;
            HasPosition = true;
            PositionClosed = false;
            _logger.LogInformation("Position opened at {Price} for {Qty} shares", currentPrice, Quantity);
        }
        
        // Update exit price to current price (tracks live P/L)
        ExitPrice = currentPrice;
        
        // Calculate position value
        PositionValue = currentPrice * Quantity;
        
        // Calculate P/L if we have a position
        if (HasPosition && EntryPrice.HasValue && EntryPrice.Value > 0)
        {
            // Use exit price if position closed, otherwise use current price
            var priceForPL = PositionClosed && ExitPrice.HasValue ? ExitPrice.Value : currentPrice;
            ProfitLoss = (priceForPL - EntryPrice.Value) * Quantity;
            ProfitLossPercent = ((priceForPL - EntryPrice.Value) / EntryPrice.Value) * 100;
            
            if (PositionClosed)
            {
                PlCalculation = $"({priceForPL:C2} - {EntryPrice.Value:C2}) × {Quantity} = {ProfitLoss:C2}";
                _logger.LogInformation("P/L (closed): {Calc}", PlCalculation);
            }
            else
            {
                PlCalculation = $"({currentPrice:C2} - {EntryPrice.Value:C2}) × {Quantity} = {ProfitLoss:C2}";
                _logger.LogInformation("P/L (open): {Calc}", PlCalculation);
            }
        }
        else
        {
            ProfitLoss = 0;
            ProfitLossPercent = 0;
        }
    }

    [RelayCommand]
    private void ResetPosition()
    {
        EntryPrice = null;
        ExitPrice = null;
        HasPosition = false;
        PositionClosed = false;
        ProfitLoss = 0;
        ProfitLossPercent = 0;
        _logger.LogInformation("Position reset");
    }

    public void Dispose()
    {
        // Unsubscribe from tick stream
        _tickSubscription?.Dispose();
        _tickSubscription = null;
        
        // Unsubscribe from candlestick builder on disposal to ensure cleanup
        UnsubscribeFromCandlestickBuilder();
        
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }
}

