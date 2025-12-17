using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MarketScanner.Services.Impl;

namespace MarketScanner.ViewModels;

public partial class AlgoRunnerViewModel : ObservableObject
{
    private readonly IAlgoStrategy _algorithm;
    private readonly ILogger<AlgoRunnerViewModel> _logger;
    private readonly ICandlestickBuilder? _candlestickBuilder;
    private readonly IbkrGatewayService? _ibkrGatewayService;
    private readonly ITradeService? _tradeService;
    private readonly Services.Impl.MacdStrategy? _macdStrategy;
    private readonly Services.Impl.LiveRsiService? _liveRsiService;
    private readonly Services.Impl.MacdEngine? _macdEngine;
    private readonly Services.Impl.RsiEngine? _rsiEngine;

    private readonly IRsiSettingsService? _rsiSettingsService;
    private readonly ICandlestickStorage? _candlestickStorage;
    private readonly Config.CandlestickConfig? _config;
    private CancellationTokenSource? _cancellationTokenSource;
    private IDisposable? _tickSubscription;
    private IDisposable? _candlestickSubscription;
    private Action<string, Candlestick>? _liveCandleUpdateHandler;
    private Action<string, decimal, DateTime>? _tickPriceHandler;
    private Action<string, Candlestick>? _finalizedCandleHandler;
    private DateTime? _entryTime;
    private int? _currentTradeId; // Track the current open trade ID
    private bool _previousWasBullish;
    private int _macdRecalcCounter;
    private readonly object _macdRecalcLock = new();
    private const MacdMode LiveMode = MacdMode.Live;
    private const MacdMode CandleMode = MacdMode.Candle;

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

    // Live RSI display property (updates on every tick)
    [ObservableProperty] private double? _liveRsiValue;

    public AlgoRunnerViewModel(
        IAlgoStrategy algorithm,
        ILogger<AlgoRunnerViewModel> logger,
        ICandlestickBuilder? candlestickBuilder = null,
        IbkrGatewayService? ibkrGatewayService = null,
        ITradeService? tradeService = null,
        Services.Impl.MacdStrategy? macdStrategy = null,
        Services.Impl.LiveRsiService? liveRsiService = null,
        Services.Impl.MacdEngine? macdEngine = null,
        Services.Impl.RsiEngine? rsiEngine = null,
        IRsiSettingsService? rsiSettingsService = null,
        ICandlestickStorage? candlestickStorage = null,
        Config.CandlestickConfig? config = null)
    {
        _algorithm = algorithm;
        _logger = logger;
        _candlestickBuilder = candlestickBuilder;
        _ibkrGatewayService = ibkrGatewayService;
        _tradeService = tradeService;
        _macdStrategy = macdStrategy;
        _liveRsiService = liveRsiService;
        _macdEngine = macdEngine;
        _rsiEngine = rsiEngine;
        _rsiSettingsService = rsiSettingsService;
        _candlestickStorage = candlestickStorage;
        _config = config;
    }

    public async Task InitializeAsync(ScannerRowViewModel symbol)
    {
        SelectedSymbol = symbol;
        ErrorMessage = string.Empty;
        Result = null;
        ResetPosition(); // Reset position on initialization

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

            // Initialize RSI state for live updates
            await InitializeRsiStateAsync();

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
            Result = await _algorithm.ExecuteAsync(SelectedSymbol, _cancellationTokenSource.Token);
            if (Result != null && SelectedSymbol != null)
            {
                SelectedSymbol.RsiValue = Result.RsiValue;
                SelectedSymbol.RsiSignal = Result.RsiSignal;
            }

            // Update peak RSI for open positions
            if (Result?.RsiValue.HasValue == true && HasPosition && !PositionClosed && _currentTradeId.HasValue)
            {
                await UpdatePeakRsiAsync(Result.RsiValue.Value);
            }

            _logger.LogInformation("Algorithm result: {Action} for {Symbol} - MACD: {Macd:F4}, Signal: {Signal:F4}",
                Result.Action, Result.Symbol, Result.Macd?.MacdLine ?? 0, Result.Macd?.SignalLine ?? 0);

            // Ensure we only buy when we don't have a position, and only sell when we have a position
            if (Result.Action == AlgoAction.Buy && HasPosition)
            {
                _logger.LogInformation("Ignoring BUY signal - already have a position");
                Result = Result with { Action = AlgoAction.Hold, Reason = "Already have position. " + Result.Reason };
            }
            else if (Result.Action == AlgoAction.Sell && !HasPosition)
            {
                _logger.LogInformation("Ignoring SELL signal - no position to close");
                Result = Result with { Action = AlgoAction.Hold, Reason = "No position to close. " + Result.Reason };
            }

            // Update MACD display
            UpdateMacdDisplay();

            // Handle position opening/closing based on algo action (only if action wasn't filtered out)
            if (Result.Action == AlgoAction.Buy && !HasPosition)
            {
                EntryPrice = (decimal)SelectedSymbol.LastPrice;
                _entryTime = DateTime.UtcNow;
                HasPosition = true;
                PositionClosed = false;
                _logger.LogInformation("Position opened at {Price:C2} for {Qty} shares (BUY signal)", EntryPrice, Quantity);

                // Save trade as open position
                await SaveTradeAsync();
            }
            else if (Result.Action == AlgoAction.Sell && HasPosition && !PositionClosed)
            {
                ExitPrice = (decimal)SelectedSymbol.LastPrice;
                PositionClosed = true;
                HasPosition = false; // Position is now closed
                _logger.LogInformation("Position closed at {Price:C2} for {Qty} shares (SELL signal)", ExitPrice, Quantity);

                // Update trade to mark as closed
                await UpdateTradeAsync();
            }

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

        // CRITICAL: Subscribe symbol to candlestick builder
        // This tells CandlestickBuilder to process ticks for this symbol and generate candlesticks
        _candlestickBuilder.SubscribeSymbol(SelectedSymbol.Symbol);
        _logger.LogInformation("Subscribed symbol {Symbol} to candlestick builder", SelectedSymbol.Symbol);

        _candlestickSubscription?.Dispose();
        _candlestickSubscription = _candlestickBuilder.CandlestickStream
            .Where(c => c.Symbol == SelectedSymbol.Symbol)
            .Subscribe(OnNewCandlestick);

        // Subscribe to live candle updates for real-time RSI (MACD only updates on finalized candles)
        _liveCandleUpdateHandler = (symbol, candle) =>
        {
            if (!IsRunning || SelectedSymbol == null || symbol != SelectedSymbol.Symbol)
                return;

            // RSI live update (MACD updates only on finalized candles via MacdEngine)
            if (_liveRsiService != null && SelectedSymbol != null)
            {
                var interval = candle.Interval;
                var rsi = _liveRsiService.LiveUpdate(symbol, interval, (double)candle.Close, candle.Timestamp);
                if (rsi.HasValue)
                {
                    UpdateLiveRsiDisplay(symbol, rsi.Value);
                }
            }
        };
        
        _candlestickBuilder.OnLiveCandleUpdated += _liveCandleUpdateHandler;

        // Subscribe to tick price and finalized candle events for MacdEngine
        if (_macdEngine != null && _rsiEngine != null && _config != null)
        {
            var interval = GetIntervalString(_config.IntervalSeconds);

            // TICK UPDATE (RSI + MACD live preview)
            _tickPriceHandler = (symbol, price, ts) =>
            {
                if (!IsRunning || SelectedSymbol == null || symbol != SelectedSymbol.Symbol)
                    return;

                // RSI updates every tick (pass timestamp)
                _rsiEngine.UpdateLive(symbol, interval, price, ts);
                
                // MACD live preview updates on every tick (if enabled)
                if (_config.EnableLiveMacdPreview)
                {
                    _macdEngine.UpdateOnTick(symbol, interval, price, ts, LiveMode);
                }
                
                // Update UI with latest MACD values from engine
                var macdResult = _config.EnableLiveMacdPreview
                    ? _macdEngine.GetLastMacd(symbol, interval, LiveMode)
                    : _macdEngine.GetLastMacd(symbol, interval, CandleMode);
                if (macdResult.HasValue)
                {
                    var (macd, signal, hist) = macdResult.Value;
                    var macdData = new MacdData(
                        Symbol: symbol,
                        MacdLine: (decimal)macd,
                        SignalLine: (decimal)signal,
                        Histogram: (decimal)hist,
                        Timestamp: ts,
                        Interval: interval
                    );
                    UpdateMacdDisplayFromLive(macdData);
                }
                
                // Update RSI display (update LiveRsiValue property for UI binding)
                var rsi = _rsiEngine.GetRsi(symbol, interval);
                if (rsi.HasValue)
                {
                    LiveRsiValue = rsi.Value; // Update live property for UI
                    UpdateLiveRsiDisplay(symbol, rsi.Value); // Keep existing for SelectedSymbol
                }
            };

            // FINALIZED CANDLE UPDATE (RSI + MACD authoritative)
            _finalizedCandleHandler = (symbol, candle) =>
            {
                if (!IsRunning || SelectedSymbol == null || symbol != SelectedSymbol.Symbol)
                    return;

                // MACD authoritative update when a candle closes
                _macdEngine.UpdateOnFinalizedCandle(symbol, candle.Interval, candle, CandleMode);

                // After close, reset live preview state to authoritative candle state
                if (_config.EnableLiveMacdPreview)
                {
                    _macdEngine.SyncState(symbol, candle.Interval, CandleMode, LiveMode);
                }

                // RSI also updates on finalized close (pass candle timestamp)
                _rsiEngine.UpdateLive(symbol, candle.Interval, candle.Close, candle.Timestamp);

                // Update UI with latest MACD values from authoritative (candle) engine
                var macdResult = _macdEngine.GetLastMacd(symbol, candle.Interval, CandleMode);
                if (macdResult.HasValue)
                {
                    var (macd, signal, hist) = macdResult.Value;
                    var macdData = new MacdData(
                        Symbol: symbol,
                        MacdLine: (decimal)macd,
                        SignalLine: (decimal)signal,
                        Histogram: (decimal)hist,
                        Timestamp: candle.Timestamp,
                        Interval: candle.Interval
                    );
                    UpdateMacdDisplayFromLive(macdData);
                }

                // Periodic MACD reconciliation to mitigate drift during long sessions
                ReconcileMacdIfNeeded(symbol, candle.Interval);
            };

            _candlestickBuilder.OnTickPrice += _tickPriceHandler;
            _candlestickBuilder.OnFinalizedCandle += _finalizedCandleHandler;
        }


        _logger.LogInformation("Subscribed to candlestick stream for continuous RSI/MACD updates on {Symbol}", SelectedSymbol.Symbol);
    }

    private void ReconcileMacdIfNeeded(string symbol, string interval)
    {
        if (_config == null || _macdEngine == null || _candlestickStorage == null)
            return;

        var every = _config.MacdRecalcEveryNCandles;
        if (every <= 0)
            return;

        _macdRecalcCounter++;
        if (_macdRecalcCounter < every)
            return;

        lock (_macdRecalcLock)
        {
            // Double-check after acquiring the lock
            if (_macdRecalcCounter < every)
                return;

            var candles = _candlestickStorage
                .GetCandlesticks(symbol, interval, int.MaxValue)
                .OrderBy(c => c.Timestamp)
                .ToList();

            if (candles.Count == 0)
            {
                _logger.LogWarning("MACD reconciliation skipped for {Symbol}: no candles available", symbol);
                _macdRecalcCounter = 0;
                return;
            }

            _macdEngine.Initialize(
                symbol,
                interval,
                candles,
                _config.Macd.FastPeriod,
                _config.Macd.SlowPeriod,
                _config.Macd.SignalPeriod,
                CandleMode);

            if (_config.EnableLiveMacdPreview)
            {
                _macdEngine.SyncState(symbol, interval, CandleMode, LiveMode);
            }

            _macdRecalcCounter = 0;
            _logger.LogInformation(
                "MACD reconciliation complete for {Symbol} using {Count} candles (every {Every} finalized candles). Live preview reset from candle state.",
                symbol,
                candles.Count,
                every);
        }
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

        // Unsubscribe from live updates
        if (_candlestickBuilder != null)
        {
            if (_liveCandleUpdateHandler != null)
            {
                _candlestickBuilder.OnLiveCandleUpdated -= _liveCandleUpdateHandler;
                _liveCandleUpdateHandler = null;
            }

            if (_tickPriceHandler != null)
            {
                _candlestickBuilder.OnTickPrice -= _tickPriceHandler;
                _tickPriceHandler = null;
            }

            if (_finalizedCandleHandler != null)
            {
                _candlestickBuilder.OnFinalizedCandle -= _finalizedCandleHandler;
                _finalizedCandleHandler = null;
            }
        }

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

                // Unsubscribe from live updates
                if (_liveCandleUpdateHandler != null)
                {
                    _candlestickBuilder.OnLiveCandleUpdated -= _liveCandleUpdateHandler;
                    _liveCandleUpdateHandler = null;
                }

                if (_tickPriceHandler != null)
                {
                    _candlestickBuilder.OnTickPrice -= _tickPriceHandler;
                    _tickPriceHandler = null;
                }

                if (_finalizedCandleHandler != null)
                {
                    _candlestickBuilder.OnFinalizedCandle -= _finalizedCandleHandler;
                    _finalizedCandleHandler = null;
                }

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

    private void UpdateMacdDisplayFromLive(MacdData macd)
    {
        MacdLine = macd.MacdLine;
        SignalLine = macd.SignalLine;
        Histogram = macd.Histogram;
        IsHistogramPositive = macd.HasPositiveHistogram;

        // Update crossover status (compare with previous values)
        bool isBullish = macd.IsBullish;
        HasCrossedUp = isBullish && !_previousWasBullish;
        HasCrossedDown = !isBullish && _previousWasBullish;
        _previousWasBullish = isBullish;

        CrossoverStatus = isBullish ? "↑ Bullish" : "↓ Bearish";
    }

    private void UpdateLiveRsiDisplay(string symbol, double rsi)
    {
        if (SelectedSymbol?.Symbol == symbol)
        {
            SelectedSymbol.RsiValue = rsi;
            // RSI signal logic can be added here if needed
            // For now, just update the value
        }
    }

    private async Task InitializeRsiStateAsync()
    {
        if (_liveRsiService == null || _rsiSettingsService == null || _candlestickStorage == null ||
            _config == null || SelectedSymbol == null)
        {
            _logger.LogDebug("AlgoRunnerViewModel: Skipping RSI state initialization - required services not available");
            return;
        }

        try
        {
            var settings = await _rsiSettingsService.GetAsync();
            var interval = GetIntervalString(_config.IntervalSeconds);

            // Get historical candlesticks from storage
            var candlesticks = _candlestickStorage.GetCandlesticks(SelectedSymbol.Symbol, interval, int.MaxValue)
                .OrderBy(c => c.Timestamp)
                .ToList();

            if (candlesticks.Count >= settings.Period + 1)
            {
                _liveRsiService.Initialize(SelectedSymbol.Symbol, interval, candlesticks, settings.Period);
                
                // Also initialize RsiEngine if available
                if (_rsiEngine != null)
                {
                    _rsiEngine.Initialize(SelectedSymbol.Symbol, interval, candlesticks, settings.Period);
                }
                
                _logger.LogInformation("AlgoRunnerViewModel: Initialized RSI state for {Symbol} with {Count} candlesticks",
                    SelectedSymbol.Symbol, candlesticks.Count);
            }
            else
            {
                _logger.LogWarning("AlgoRunnerViewModel: Insufficient candlesticks for RSI initialization. Need {Required}, got {Actual}",
                    settings.Period + 1, candlesticks.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AlgoRunnerViewModel: Error initializing RSI state for {Symbol}", SelectedSymbol?.Symbol);
        }
    }

    private string GetIntervalString(int intervalSeconds)
    {
        return intervalSeconds switch
        {
            15 => "15s",
            30 => "30s",
            60 => "1min",
            _ => $"{intervalSeconds}s"
        };
    }

    private void UpdateProfitLoss()
    {
        if (SelectedSymbol == null)
            return;

        var currentPrice = (decimal)SelectedSymbol.LastPrice;

        // Don't auto-open position - wait for BUY signal from algorithm
        // Only update exit price if we have an open position
        if (HasPosition && !PositionClosed)
        {
            ExitPrice = currentPrice;
        }

        // Calculate position value only if we have a position
        if (HasPosition && EntryPrice.HasValue)
        {
            PositionValue = currentPrice * Quantity;
        }
        else
        {
            PositionValue = 0;
        }

        // Calculate P/L if we have a position (open or closed)
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
            PlCalculation = "No position";
        }
    }

    [RelayCommand]
    private void ResetPosition()
    {
        EntryPrice = null;
        ExitPrice = null;
        HasPosition = false;
        PositionClosed = false;
        _entryTime = null;
        _currentTradeId = null;
        ProfitLoss = 0;
        ProfitLossPercent = 0;
        _logger.LogInformation("Position reset");
    }

    private async Task SaveTradeAsync()
    {
        if (_tradeService == null || SelectedSymbol == null || !EntryPrice.HasValue || !_entryTime.HasValue)
        {
            _logger.LogWarning("Cannot save trade: missing required data or service");
            return;
        }

        try
        {
            var currentPrice = (decimal)SelectedSymbol.LastPrice;
            var profitLoss = (currentPrice - EntryPrice.Value) * Quantity;
            var profitLossPercent = EntryPrice.Value > 0
                ? ((currentPrice - EntryPrice.Value) / EntryPrice.Value) * 100
                : 0;

            // Get RSI settings to calculate stop-loss and activation price
            var serviceProvider = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;
            var rsiSettingsService = serviceProvider?.GetService<IRsiSettingsService>();
            var rsiSettings = rsiSettingsService != null ? await rsiSettingsService.GetAsync() : null;

            // Calculate initial stop-loss price
            var initialStopLossPercent = rsiSettings?.InitialStopLossPercent ?? 2.0;
            var initialStopLossPrice = EntryPrice.Value * (1 - (decimal)(initialStopLossPercent / 100.0));

            // Calculate trailing stop activation price
            var activationPercent = rsiSettings?.TrailingStopActivationPercent ?? 2.0;
            var activationPrice = EntryPrice.Value * (1 + (decimal)(activationPercent / 100.0));

            var trade = new Trade
            {
                Symbol = SelectedSymbol.Symbol,
                EntryPrice = EntryPrice.Value,
                ExitPrice = null, // Open position
                Quantity = Quantity,
                ProfitLoss = profitLoss, // Current unrealized P/L
                ProfitLossPercent = profitLossPercent,
                EntryTime = _entryTime.Value,
                ExitTime = null, // Open position
                Status = TradeStatus.Open,
                CurrentPrice = currentPrice,
                AlgorithmName = _algorithm.Name,
                PeakRsiValue = Result?.RsiValue, // Initialize peak RSI with entry RSI if available
                HighestPrice = EntryPrice.Value, // Initialize highest price with entry price
                InitialStopLossPrice = initialStopLossPrice,
                TrailingStopActivationPrice = activationPrice,
                TrailingStopActivated = false // Will activate when price reaches activationPrice
            };

            await _tradeService.SaveTradeAsync(trade);

            // Query for the trade ID after insertion (SQLite-net should update Id, but query to be safe)
            if (trade.Id == 0)
            {
                var openTrades = await _tradeService.GetOpenTradesAsync();
                var latestTrade = openTrades
                    .Where(t => t.Symbol == SelectedSymbol.Symbol &&
                               Math.Abs((t.EntryTime - _entryTime.Value).TotalSeconds) < 1) // Match within 1 second
                    .OrderByDescending(t => t.EntryTime)
                    .FirstOrDefault();
                if (latestTrade != null)
                {
                    _currentTradeId = latestTrade.Id;
                }
            }
            else
            {
                _currentTradeId = trade.Id;
            }

            _logger.LogInformation("Trade saved (OPEN): {Symbol} Entry={EntryPrice:C2} Status={Status} TradeId={TradeId}",
                trade.Symbol, trade.EntryPrice, trade.Status, _currentTradeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save trade for {Symbol}", SelectedSymbol.Symbol);
            // Don't throw - allow algo to continue even if trade save fails
        }
    }

    private async Task UpdateTradeAsync()
    {
        if (_tradeService == null || SelectedSymbol == null || !EntryPrice.HasValue || !ExitPrice.HasValue || !_entryTime.HasValue || !_currentTradeId.HasValue)
        {
            _logger.LogWarning("Cannot update trade: missing required data or service");
            return;
        }

        try
        {
            // Get the existing trade
            var trades = await _tradeService.GetTradesBySymbolAsync(SelectedSymbol.Symbol);
            var trade = trades.FirstOrDefault(t => t.Id == _currentTradeId.Value && t.Status == TradeStatus.Open);

            if (trade == null)
            {
                _logger.LogWarning("Cannot find open trade with ID {TradeId} for {Symbol}", _currentTradeId.Value, SelectedSymbol.Symbol);
                return;
            }

            // Update the trade to mark as closed
            trade.ExitPrice = ExitPrice.Value;
            trade.ExitTime = DateTime.UtcNow;
            trade.Status = TradeStatus.Closed;
            trade.ProfitLoss = ProfitLoss;
            trade.ProfitLossPercent = ProfitLossPercent;
            trade.CurrentPrice = null; // No longer needed for closed trades

            await _tradeService.UpdateTradeAsync(trade);
            _logger.LogInformation("Trade updated (CLOSED): {Symbol} Entry={EntryPrice:C2} Exit={ExitPrice:C2} P/L={PL:C2}",
                trade.Symbol, trade.EntryPrice, trade.ExitPrice, trade.ProfitLoss);

            _currentTradeId = null; // Clear the trade ID
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update trade for {Symbol}", SelectedSymbol.Symbol);
            // Don't throw - allow algo to continue even if trade update fails
        }
    }

    private decimal? _lastUpdatePrice; // Track last price used for update
    private const decimal UpdateThreshold = 0.002m; // 0.2% threshold for database updates

    private async Task UpdatePeakRsiAsync(double currentRsi)
    {
        if (_tradeService == null || SelectedSymbol == null || !_currentTradeId.HasValue)
        {
            return;
        }

        try
        {
            var currentPrice = (decimal)SelectedSymbol.LastPrice;

            // OPTIMIZATION: Only update if price changed significantly (>0.2%)
            if (_lastUpdatePrice.HasValue)
            {
                var priceChange = Math.Abs(currentPrice - _lastUpdatePrice.Value) / _lastUpdatePrice.Value;
                if (priceChange < UpdateThreshold)
                {
                    return; // Skip update - price change too small
                }
            }

            // Get the existing trade
            var trades = await _tradeService.GetTradesBySymbolAsync(SelectedSymbol.Symbol);
            var trade = trades.FirstOrDefault(t => t.Id == _currentTradeId.Value && t.Status == TradeStatus.Open);

            if (trade == null)
            {
                return; // Trade not found or already closed
            }

            bool needsUpdate = false;

            // Update peak RSI if current RSI is higher
            if (!trade.PeakRsiValue.HasValue || currentRsi > trade.PeakRsiValue.Value)
            {
                trade.PeakRsiValue = currentRsi;
                needsUpdate = true;
            }

            // Update highest price for trailing stop (only if trailing stop is activated)
            // Note: Highest price updates for trailing stop are now handled in CheckTrailingStopAsync
            // This method only updates RSI tracking

            // Only update database if something changed
            if (needsUpdate)
            {
                await _tradeService.UpdateTradeAsync(trade);
                _lastUpdatePrice = currentPrice; // Track last update price
                _logger.LogInformation("Updated trade state for {Symbol}: PeakRSI={PeakRsi:F2}",
                    SelectedSymbol.Symbol, trade.PeakRsiValue);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update peak RSI for {Symbol}", SelectedSymbol?.Symbol);
            // Don't throw - allow algo to continue even if update fails
        }
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

