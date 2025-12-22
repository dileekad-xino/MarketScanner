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
    private readonly Services.Impl.MacdEngine? _macdEngine;
    private readonly Services.Impl.RsiEngine? _rsiEngine;

    private readonly IRsiSettingsService? _rsiSettingsService;
    private readonly ICandlestickStorage? _candlestickStorage;
    private readonly Config.CandlestickConfig? _config;
    private CancellationTokenSource? _cancellationTokenSource;
    private IDisposable? _tickSubscription;
    // Live-only: no candle stream subscription for strategy evaluation
    private Action<string, decimal, DateTime>? _tickPriceHandler;
    private Action<string, Candlestick>? _finalizedCandleHandler;
    private DateTime? _entryTime;
    private int? _currentTradeId; // Track the current open trade ID
    private bool _previousWasBullish;
    // Live-only pipeline: remove MACD reconciliation + mode flags
    private int _tickEvalInFlight;
    private int _tickEvalPending;

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

            // Subscribe to tick-driven evaluation/indicator updates
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

    [RelayCommand]
    private void StopAlgo()
    {
        try
        {
            _logger.LogInformation("Stopping algorithm for {Symbol}", SelectedSymbol?.Symbol);
            
            // Cancel execution
            _cancellationTokenSource?.Cancel();
            
            // Unsubscribe from updates
            UnsubscribeFromCandlestickBuilder();
            _tickSubscription?.Dispose();
            _tickSubscription = null;
            
            // Update state
            IsRunning = false;
            ErrorMessage = string.Empty;
            
            _logger.LogInformation("Algorithm stopped successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping algorithm");
            ErrorMessage = $"Error stopping algorithm: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Close()
    {
        try
        {
            _logger.LogInformation("Closing AlgoRunner page for {Symbol} (algo continues in background: {IsRunning})", 
                SelectedSymbol?.Symbol, IsRunning);
            
            // Navigate back (dismiss modal)
            if (Application.Current?.MainPage != null)
            {
                await Application.Current.MainPage.Navigation.PopModalAsync();
            }
            
            // Note: Dispose() is NOT called here - algorithm keeps running in background
            _logger.LogInformation("AlgoRunner page closed, algorithm still running: {IsRunning}", IsRunning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing AlgoRunner page");
            ErrorMessage = $"Error closing page: {ex.Message}";
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

            // Align Reason with current indicator values after strategy run
            UpdateReasonWithIndicatorValues();

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

        // Live-only pipeline: no candle-derived RSI updates

        // Subscribe to tick price events for live-only MACD/RSI + strategy evaluation
        if (_macdEngine != null && _rsiEngine != null && _config != null)
        {
            var interval = GetIntervalString(_config.IntervalSeconds);

            // TICK UPDATE (RSI preview + MACD preview)
            _tickPriceHandler = (symbol, price, ts) =>
            {
                if (!IsRunning || SelectedSymbol == null || symbol != SelectedSymbol.Symbol)
                    return;

                // RSI preview updates every tick (doesn't modify committed state)
                _rsiEngine.UpdateOnTick(symbol, interval, price, ts);
                
                // MACD updates on every tick (live-only preview)
                _macdEngine.UpdateOnTick(symbol, interval, price, ts);
                
                // Update UI with latest MACD values from engine
                var macdResult = _macdEngine.GetLastMacd(symbol, interval);
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
                
                // Update RSI display (shows preview RSI for live intrabar updates)
                var rsi = _rsiEngine.GetRsi(symbol, interval);
                if (rsi.HasValue)
                {
                    LiveRsiValue = rsi.Value; // Update live property for UI (preview RSI)
                    UpdateLiveRsiDisplay(symbol, rsi.Value); // Keep existing for SelectedSymbol
                }

                ScheduleTickEvaluation();
            };

            _candlestickBuilder.OnTickPrice += _tickPriceHandler;

            // Commit RSI and MACD baseline on each finalized candle close (prevents long-run drift while keeping tick preview)
            _finalizedCandleHandler = (symbol, candle) =>
            {
                if (!IsRunning || SelectedSymbol == null || symbol != SelectedSymbol.Symbol)
                    return;

                // Optional MACD debug snapshot: preview (last tick) vs committed (candle close)
                double? previewMacdBefore = null;
                double? previewSignalBefore = null;
                double? previewHistBefore = null;
                double committedMacdBefore = 0;
                double committedSignalBefore = 0;
                if (_config.EnableMacdDebugLogging && _macdEngine.TryGetState(symbol, candle.Interval, out var stateBefore))
                {
                    previewMacdBefore = stateBefore.LiveMacd;
                    previewSignalBefore = stateBefore.LiveSignal;
                    previewHistBefore = stateBefore.LiveHist;
                    committedMacdBefore = stateBefore.Macd;
                    committedSignalBefore = stateBefore.Signal;
                }

                // Commit RSI on candle close (matches TradingView - authoritative update)
                _rsiEngine.UpdateOnFinalizedCandle(symbol, candle.Interval, candle.Close, candle.Timestamp);

                // Commit MACD on candle close
                _macdEngine.UpdateOnFinalizedCandle(symbol, candle.Interval, candle.Close, candle.Timestamp);

                // Update UI immediately to the committed close snapshot
                var macdResult = _macdEngine.GetLastMacd(symbol, candle.Interval);
                if (macdResult.HasValue)
                {
                    var (macd, signal, hist) = macdResult.Value;
                    UpdateMacdDisplayFromLive(new MacdData(
                        Symbol: symbol,
                        MacdLine: (decimal)macd,
                        SignalLine: (decimal)signal,
                        Histogram: (decimal)hist,
                        Timestamp: candle.Timestamp,
                        Interval: candle.Interval
                    ));
                }

                // Update RSI display to committed value (matches TradingView)
                var rsi = _rsiEngine.GetRsi(symbol, candle.Interval);
                if (rsi.HasValue)
                {
                    LiveRsiValue = rsi.Value; // Now shows committed RSI (matches TradingView)
                    UpdateLiveRsiDisplay(symbol, rsi.Value);
                }

                if (_config.EnableMacdDebugLogging && _macdEngine.TryGetState(symbol, candle.Interval, out var stateAfter))
                {
                    var committedMacdAfter = stateAfter.Macd;
                    var committedSignalAfter = stateAfter.Signal;
                    var committedHistAfter = committedMacdAfter - committedSignalAfter;

                    // Compare last preview vs committed close (helps verify TV-style intrabar preview)
                    var dm = previewMacdBefore.HasValue ? Math.Abs(previewMacdBefore.Value - committedMacdAfter) : (double?)null;
                    var ds = previewSignalBefore.HasValue ? Math.Abs(previewSignalBefore.Value - committedSignalAfter) : (double?)null;

                    _logger.LogInformation(
                        "MACD Debug [{Symbol} {Interval}] CloseTs={Ts:o} Close={Close:F4} | PreviewBefore M={PM:F6} S={PS:F6} H={PH:F6} | CommittedBefore M={CBM:F6} S={CBS:F6} | CommittedAfter M={CAM:F6} S={CAS:F6} H={CAH:F6} | ΔPreviewVsClose M={DM:F6} S={DS:F6}",
                        symbol,
                        candle.Interval,
                        candle.Timestamp,
                        candle.Close,
                        previewMacdBefore ?? double.NaN,
                        previewSignalBefore ?? double.NaN,
                        previewHistBefore ?? double.NaN,
                        committedMacdBefore,
                        committedSignalBefore,
                        committedMacdAfter,
                        committedSignalAfter,
                        committedHistAfter,
                        dm ?? double.NaN,
                        ds ?? double.NaN
                    );
                }
            };

            _candlestickBuilder.OnFinalizedCandle += _finalizedCandleHandler;
        }


        _logger.LogInformation("Subscribed to candlestick stream for continuous RSI/MACD updates on {Symbol}", SelectedSymbol.Symbol);
    }

    private void ScheduleTickEvaluation()
    {
        if (!IsRunning || SelectedSymbol == null)
            return;

        // Mark that we need to run (or rerun) evaluation.
        Interlocked.Exchange(ref _tickEvalPending, 1);

        // If already running, we'll be picked up when the current run finishes.
        if (Interlocked.CompareExchange(ref _tickEvalInFlight, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _tickEvalPending, 0) == 1)
                {
                    await ExecuteAlgoOnceAsync();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _tickEvalInFlight, 0);
            }
        });
    }

    // Live-only pipeline: no periodic MACD reconciliation

    private void UnsubscribeFromCandlestickBuilder()
    {
        if (_candlestickBuilder != null && SelectedSymbol != null)
        {
            try
            {
                _candlestickBuilder.UnsubscribeSymbol(SelectedSymbol.Symbol);

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

        // Keep Reason in sync with the same values shown in the indicator
        UpdateReasonWithIndicatorValues();
    }

    private void UpdateReasonWithIndicatorValues()
    {
        if (Result == null)
            return;

        // Prefer live display values; fallback to candle/strategy
        var macdPart = $"MACD={MacdLine:F4}, Signal={SignalLine:F4}, Hist={Histogram:F4}";
        string rsiPart;
        if (LiveRsiValue.HasValue)
            rsiPart = $"RSI={LiveRsiValue.Value:F2}";
        else if (Result.RsiValue.HasValue)
            rsiPart = $"RSI={Result.RsiValue.Value:F2}";
        else
            rsiPart = "RSI=N/A";

        var baseReason = Result.Reason ?? string.Empty;
        var idxMon = baseReason.IndexOf("Monitoring:", StringComparison.OrdinalIgnoreCase);
        if (idxMon >= 0)
            baseReason = baseReason[..idxMon].TrimEnd();

        var monitoringPart = $"Monitoring: {macdPart}; {rsiPart}";
        var combined = string.IsNullOrWhiteSpace(baseReason) ? monitoringPart : $"{baseReason} | {monitoringPart}";

        Result = Result with { Reason = combined };
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
        if (_rsiSettingsService == null || _candlestickStorage == null ||
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
                // Initialize RsiEngine from historical warmup once
                _rsiEngine?.Initialize(SelectedSymbol.Symbol, interval, candlesticks, settings.Period);
                
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
        // Unsubscribe from tick stream
        _tickSubscription?.Dispose();
        _tickSubscription = null;

        // Unsubscribe from candlestick builder on disposal to ensure cleanup
        UnsubscribeFromCandlestickBuilder();

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }
}

