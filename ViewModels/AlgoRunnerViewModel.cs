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
    private IDisposable? _candlestickSubscription;

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
    
    // MACD display properties
    [ObservableProperty] private decimal _macdLine;
    [ObservableProperty] private decimal _signalLine;
    [ObservableProperty] private decimal _histogram;
    [ObservableProperty] private bool _isHistogramPositive;
    [ObservableProperty] private string _crossoverStatus = "No Crossover";
    [ObservableProperty] private bool _hasCrossedUp;
    [ObservableProperty] private bool _hasCrossedDown;

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

            // Cancel any previous execution
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("Starting continuous monitoring for {Symbol} with algorithm {AlgorithmName}", 
                SelectedSymbol.Symbol, _algorithm.Name);

            // Run initial algo execution
            await ExecuteAlgoOnceAsync();

            // Subscribe to candlestick stream for continuous updates
            SubscribeToCandlestickStream();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Algorithm execution was cancelled");
            ErrorMessage = "Algorithm execution was cancelled";
            IsRunning = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing algorithm");
            ErrorMessage = $"Error executing algorithm: {ex.Message}";
            IsRunning = false;
        }
    }

    private async Task ExecuteAlgoOnceAsync()
    {
        if (SelectedSymbol == null || _cancellationTokenSource?.IsCancellationRequested == true)
            return;

        try
        {
            Result = await _algorithm.ExecuteAsync(SelectedSymbol, _cancellationTokenSource?.Token ?? CancellationToken.None);

            _logger.LogInformation("Algorithm result: {Action} for {Symbol} - MACD: {Macd:F4}, Signal: {Signal:F4}", 
                Result.Action, Result.Symbol, Result.Macd?.MacdLine ?? 0, Result.Macd?.SignalLine ?? 0);

            // Update MACD display
            UpdateMacdDisplay();

            // Update P/L calculations
            UpdateProfitLoss();
        }
        catch (OperationCanceledException)
        {
            // Ignore - expected when stopping
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in algo execution");
        }
    }

    private void SubscribeToCandlestickStream()
    {
        if (_candlestickBuilder == null || SelectedSymbol == null)
            return;

        _candlestickSubscription?.Dispose();
        _candlestickSubscription = _candlestickBuilder.CandlestickStream
            .Where(c => c.Symbol == SelectedSymbol.Symbol)
            .Subscribe(OnNewCandlestick);

        _logger.LogInformation("Subscribed to candlestick stream for continuous MACD updates on {Symbol}", SelectedSymbol.Symbol);
    }

    private async void OnNewCandlestick(Candlestick candlestick)
    {
        if (!IsRunning || SelectedSymbol == null || candlestick.Symbol != SelectedSymbol.Symbol)
            return;

        _logger.LogInformation("New candlestick for {Symbol}, re-running algorithm", candlestick.Symbol);
        await ExecuteAlgoOnceAsync();
    }

    [RelayCommand]
    private void StopAlgo()
    {
        _logger.LogInformation("Stopping algorithm monitoring for {Symbol}", SelectedSymbol?.Symbol);
        
        // Stop candlestick subscription
        _candlestickSubscription?.Dispose();
        _candlestickSubscription = null;
        
        // Cancel any pending execution
        _cancellationTokenSource?.Cancel();
        
        IsRunning = false;
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        // NOTE: Do NOT stop the algo when closing - it keeps running in background
        // Only close the page UI
        _logger.LogInformation("Closing algo runner window (algo continues running in background)");
        
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

    private void UpdateMacdDisplay()
    {
        if (Result?.Macd == null)
            return;

        MacdLine = Result.Macd.MacdLine;
        SignalLine = Result.Macd.SignalLine;
        Histogram = Result.Macd.Histogram;
        IsHistogramPositive = Result.Macd.HasPositiveHistogram;

        // Update crossover status
        HasCrossedUp = Result.Crossover == Models.CrossoverStatus.CrossedUp;
        HasCrossedDown = Result.Crossover == Models.CrossoverStatus.CrossedDown;
        
        CrossoverStatus = Result.Crossover switch
        {
            Models.CrossoverStatus.CrossedUp => "↑ Crossed Up",
            Models.CrossoverStatus.CrossedDown => "↓ Crossed Down",
            _ => "No Crossover"
        };
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
        // Stop algo monitoring
        _candlestickSubscription?.Dispose();
        _candlestickSubscription = null;
        
        // Unsubscribe from tick stream
        _tickSubscription?.Dispose();
        _tickSubscription = null;
        
        // Unsubscribe from candlestick builder on disposal to ensure cleanup
        UnsubscribeFromCandlestickBuilder();
        
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }
}

