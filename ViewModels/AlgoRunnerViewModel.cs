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
    private readonly Services.Impl.CciEngine? _cciEngine;
    private readonly ICciSettingsService? _cciSettingsService;
    private readonly Services.Impl.EmaEngine? _emaEngine;
    private readonly IDispatcherService? _dispatcher;
    private CancellationTokenSource? _cancellationTokenSource;
    private IDisposable? _tickSubscription;
    private IDisposable? _streamingBarSubscription;
    // Live-only: no candle stream subscription for strategy evaluation
    private Action<string, decimal, DateTime>? _tickPriceHandler;
    private Action<string, Candlestick>? _streamingBarHandler;
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

    // Live CCI display property (updates on every tick)
    [ObservableProperty] private double? _liveCciValue;

    // EMA 20 display properties
    [ObservableProperty] private decimal _ema20Value;
    [ObservableProperty] private string _ema20Signal = "Hold";
    [ObservableProperty] private bool _isEma20AbovePrice;

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
        Config.CandlestickConfig? config = null,
        Services.Impl.CciEngine? cciEngine = null,
        ICciSettingsService? cciSettingsService = null,
        Services.Impl.EmaEngine? emaEngine = null,
        IDispatcherService? dispatcher = null)
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
        _cciEngine = cciEngine;
        _cciSettingsService = cciSettingsService;
        _emaEngine = emaEngine;
        _dispatcher = dispatcher;
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

        // Marshal to UI thread for property updates
        _dispatcher?.OnUI(() =>
        {
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
        });
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

            // Initialize CCI state for live updates
            await InitializeCciStateAsync();

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

            // Cancel streaming historical bars
            if (SelectedSymbol != null && _ibkrGatewayService != null)
            {
                _ibkrGatewayService.CancelStreamingHistoricalBars(SelectedSymbol.Symbol);
            }

            // Cancel execution
            _cancellationTokenSource?.Cancel();

            // Unsubscribe from updates
            UnsubscribeFromCandlestickBuilder();
            _tickSubscription?.Dispose();
            _tickSubscription = null;
            _streamingBarSubscription?.Dispose();
            _streamingBarSubscription = null;

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

    // Command for closing tile (used when embedded as tile)
    // The actual removal will be handled by AlgoRunnerManagerService
    public event EventHandler? CloseTileRequested;

    [RelayCommand]
    private void CloseTile()
    {
        CloseTileRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteAlgoOnceAsync()
    {
        if (SelectedSymbol == null || _cancellationTokenSource?.IsCancellationRequested == true)
            return;

        try
        {
            bool hasOpenPosition = HasPosition && !PositionClosed;
            Result = await _algorithm.ExecuteAsync(SelectedSymbol, hasOpenPosition, _cancellationTokenSource.Token);
            if (Result != null && SelectedSymbol != null)
            {
                SelectedSymbol.RsiValue = Result.RsiValue;
                SelectedSymbol.RsiSignal = Result.RsiSignal;
                SelectedSymbol.CciValue = Result.CciValue;
                SelectedSymbol.CciSignal = Result.CciSignal;
            }

            if (Result == null)
                return;

            // Capture position state on UI thread before processing
            // Use fallback if dispatcher is unavailable or main thread is gone
            (bool hadPosition, bool wasPositionClosed, int? tradeId) positionState;
            try
            {
                positionState = await (_dispatcher?.OnUIAsync(() =>
                    (HasPosition, PositionClosed, _currentTradeId))
                    ?? Task.FromResult((HasPosition, PositionClosed, _currentTradeId)));
            }
            catch (InvalidOperationException)
            {
                // Main thread not available - use safe fallback values
                // This can happen during app shutdown
                positionState = (false, false, null);
            }

            var hadPosition = positionState.Item1;
            var wasPositionClosed = positionState.Item2;
            var tradeId = positionState.Item3;

            var shouldOpenPosition = Result.Action == AlgoAction.Buy && !hadPosition;
            var shouldClosePosition = Result.Action == AlgoAction.Sell && hadPosition && !wasPositionClosed;

            // Ensure we only buy when we don't have a position, and only sell when we have a position
            if (Result.Action == AlgoAction.Buy && hadPosition)
            {
                _logger.LogInformation("Ignoring BUY signal - already have a position");
                Result = Result with { Action = AlgoAction.Hold, Reason = "Already have position. " + Result.Reason };
            }
            else if (Result.Action == AlgoAction.Sell && !hadPosition)
            {
                _logger.LogInformation("Ignoring SELL signal - no position to close");
                Result = Result with { Action = AlgoAction.Hold, Reason = "No position to close. " + Result.Reason };
            }

            _logger.LogInformation("Algorithm result: {Action} for {Symbol} - MACD: {Macd:F4}, Signal: {Signal:F4}",
                Result.Action, Result.Symbol, Result.Macd?.MacdLine ?? 0, Result.Macd?.SignalLine ?? 0);

            // Marshal property updates to UI thread
            if (_dispatcher != null)
            {
                await _dispatcher.OnUIAsync(() =>
                {
                    if (Result != null && SelectedSymbol != null)
                    {
                        SelectedSymbol.RsiValue = Result.RsiValue;
                        SelectedSymbol.RsiSignal = Result.RsiSignal;
                    }

                    // Update MACD display
                    UpdateMacdDisplay();

                    // Update EMA 20 display
                    UpdateEma20Display();

                    // Align Reason with current indicator values after strategy run
                    UpdateReasonWithIndicatorValues();

                    // Handle position opening/closing based on algo action (only if action wasn't filtered out)
                    if (shouldOpenPosition)
                    {
                        EntryPrice = (decimal)SelectedSymbol.LastPrice;
                        _entryTime = DateTime.UtcNow;
                        HasPosition = true;
                        PositionClosed = false;
                        _logger.LogInformation("Position opened at {Price:C2} for {Qty} shares (BUY signal)", EntryPrice, Quantity);
                    }
                    else if (shouldClosePosition)
                    {
                        ExitPrice = (decimal)SelectedSymbol.LastPrice;
                        PositionClosed = true;
                        HasPosition = false; // Position is now closed
                        _logger.LogInformation("Position closed at {Price:C2} for {Qty} shares (SELL signal)", ExitPrice, Quantity);
                    }

                    // Update P/L calculations
                    UpdateProfitLoss();
                });
            }
            else
            {
                // Fallback if no dispatcher
                if (Result != null && SelectedSymbol != null)
                {
                    SelectedSymbol.RsiValue = Result.RsiValue;
                    SelectedSymbol.RsiSignal = Result.RsiSignal;
                }
                UpdateMacdDisplay();
                UpdateEma20Display();
                UpdateReasonWithIndicatorValues();
                if (shouldOpenPosition)
                {
                    EntryPrice = (decimal)SelectedSymbol.LastPrice;
                    _entryTime = DateTime.UtcNow;
                    HasPosition = true;
                    PositionClosed = false;
                }
                else if (shouldClosePosition)
                {
                    ExitPrice = (decimal)SelectedSymbol.LastPrice;
                    PositionClosed = true;
                    HasPosition = false;
                }
                UpdateProfitLoss();
            }

            // Handle trade saving (async, doesn't need UI thread, but wait for UI updates)
            if (shouldOpenPosition)
            {
                await SaveTradeAsync();
            }
            else if (shouldClosePosition)
            {
                await UpdateTradeAsync();
            }

            // Update peak RSI for open positions (async, doesn't need UI thread)
            if (Result.RsiValue.HasValue && shouldOpenPosition && tradeId.HasValue)
            {
                await UpdatePeakRsiAsync(Result.RsiValue.Value);
            }
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
        // This tells CandlestickBuilder to process ticks/bars for this symbol and generate candlesticks
        _candlestickBuilder.SubscribeSymbol(SelectedSymbol.Symbol);
        _logger.LogInformation("Subscribed symbol {Symbol} to candlestick builder", SelectedSymbol.Symbol);

        // Request streaming historical bars for MACD and RSI (bar size must match candlestick/indicator interval)
        if (_ibkrGatewayService != null)
        {
            int barSeconds = _config?.IntervalSeconds ?? 60;
            _ibkrGatewayService.RequestStreamingHistoricalBars(
                SelectedSymbol.Symbol,
                barSizeSeconds: barSeconds,
                days: 1             // Get 1 day of history + live updates
            );
            _logger.LogInformation("Requested streaming historical bars for {Symbol} ({BarSeconds}s bars) for MACD and RSI", SelectedSymbol.Symbol, barSeconds);
        }

        // Start candlestick builder with streaming bars enabled
        _candlestickBuilder.Start(useStreamingBars: true);

        // Subscribe to streaming bars for MACD, RSI, and EMA 20 updates
        if (_macdEngine != null && _rsiEngine != null && _config != null)
        {
            var interval = GetIntervalString(_config.IntervalSeconds);

            // STREAMING BAR UPDATE (MACD + RSI preview from bar close prices)
            _streamingBarHandler = (symbol, bar) =>
            {
                if (!IsRunning || SelectedSymbol == null || symbol != SelectedSymbol.Symbol)
                    return;

                // MACD updates on every bar close (more stable than tick-by-tick)
                _macdEngine.UpdateOnBar(symbol, interval, bar.Close, bar.Timestamp);

                // RSI updates on every bar close (more stable than tick-by-tick)
                _rsiEngine.UpdateOnBar(symbol, interval, bar.Close, bar.Timestamp);
                // CCI updates on every bar close (needs High, Low, Close)
                _cciEngine?.UpdateOnBar(symbol, interval, bar.High, bar.Low, bar.Close, bar.Timestamp);

                // EMA 20 updates on every bar close (real-time monitoring)
                _emaEngine?.UpdateOnBar(symbol, interval, 20, bar.Close, bar.Timestamp);

                // Update UI with latest MACD values from engine
                // Update UI with latest MACD values from engine (marshal to UI thread)
                var macdResult = _macdEngine.GetLastMacd(symbol, interval);
                if (macdResult.HasValue)
                {
                    var (macd, signal, hist) = macdResult.Value;
                    var macdData = new MacdData(
                        Symbol: symbol,
                        MacdLine: (decimal)macd,
                        SignalLine: (decimal)signal,
                        Histogram: (decimal)hist,
                        Timestamp: bar.Timestamp,
                        Interval: interval
                    );
                    _dispatcher?.OnUI(() => UpdateMacdDisplayFromLive(macdData));
                }

                // Update RSI display (shows preview RSI for live intrabar updates)
                var rsi = _rsiEngine.GetRsi(symbol, interval);
                if (rsi.HasValue)
                {
                    _dispatcher?.OnUI(() =>
                    {
                        LiveRsiValue = rsi.Value; // Update live property for UI (preview RSI)
                        UpdateLiveRsiDisplay(symbol, rsi.Value);
                    });
                }

                // Update CCI display (shows preview CCI for live intrabar updates)
                var cci = _cciEngine?.GetCci(symbol, interval);
                if (cci.HasValue)
                {
                    LiveCciValue = cci.Value; // Update live property for UI (preview CCI)
                    UpdateLiveCciDisplay(symbol, cci.Value);
                }

                // Update EMA 20 display (shows preview EMA 20 for live intrabar updates)
                var ema20 = _emaEngine?.GetEma(symbol, interval, 20);
                if (ema20.HasValue)
                {
                    _dispatcher?.OnUI(() => UpdateEma20DisplayFromLive(symbol, ema20.Value, bar.Close));
                }

                ScheduleTickEvaluation();
            };

            // Subscribe to streaming bars for MACD and RSI
            _streamingBarSubscription = _ibkrGatewayService?.StreamingBarStream
                .Where(bar => bar.Symbol == SelectedSymbol.Symbol)
                .Subscribe(bar => _streamingBarHandler?.Invoke(bar.Symbol, bar));

            // TICK UPDATE (Optional fallback - both MACD and RSI now use streaming bars)
            // Keep minimal tick handler for potential fallback scenarios
            _tickPriceHandler = (symbol, price, ts) =>
            {
                if (!IsRunning || SelectedSymbol == null || symbol != SelectedSymbol.Symbol)
                    return;

                // Note: MACD and RSI now use streaming bars via _streamingBarHandler
                // This tick handler can be used for other tick-based logic if needed
                // or removed entirely if not needed

                ScheduleTickEvaluation();
            };

            _candlestickBuilder.OnTickPrice += _tickPriceHandler;

            // Commit RSI and MACD baseline on each finalized candle close (prevents long-run drift while keeping bar preview)
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

                // Commit CCI on candle close (needs High, Low, Close)
                _cciEngine?.UpdateOnFinalizedCandle(symbol, candle.Interval, candle.High, candle.Low, candle.Close, candle.Timestamp);

                // Commit EMA 20 on candle close
                _emaEngine?.UpdateOnFinalizedCandle(symbol, candle.Interval, 20, candle.Close, candle.Timestamp);

                // Update UI immediately to the committed close snapshot
                // Update UI immediately to the committed close snapshot (marshal to UI thread)
                var macdResult = _macdEngine.GetLastMacd(symbol, candle.Interval);
                if (macdResult.HasValue)
                {
                    var (macd, signal, hist) = macdResult.Value;
                    _dispatcher?.OnUI(() => UpdateMacdDisplayFromLive(new MacdData(
                        Symbol: symbol,
                        MacdLine: (decimal)macd,
                        SignalLine: (decimal)signal,
                        Histogram: (decimal)hist,
                        Timestamp: candle.Timestamp,
                        Interval: candle.Interval
                    )));
                }

                // Update RSI display to committed value (matches TradingView)
                var rsi = _rsiEngine.GetRsi(symbol, candle.Interval);
                if (rsi.HasValue)
                {
                    _dispatcher?.OnUI(() =>
                    {
                        LiveRsiValue = rsi.Value; // Now shows committed RSI (matches TradingView)
                        UpdateLiveRsiDisplay(symbol, rsi.Value);
                    });
                }

                // Update CCI display to committed value (matches TradingView)
                var cci = _cciEngine?.GetCci(symbol, candle.Interval);
                if (cci.HasValue)
                {
                    LiveCciValue = cci.Value; // Now shows committed CCI (matches TradingView)
                    UpdateLiveCciDisplay(symbol, cci.Value);
                }

                // Update EMA 20 display to committed value (matches TradingView)
                var ema20 = _emaEngine?.GetEma(symbol, candle.Interval, 20);
                if (ema20.HasValue)
                {
                    _dispatcher?.OnUI(() => UpdateEma20DisplayFromLive(symbol, ema20.Value, candle.Close));
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

                if (_streamingBarSubscription != null)
                {
                    _streamingBarSubscription.Dispose();
                    _streamingBarSubscription = null;
                    _streamingBarHandler = null;
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

        string cciPart;
        if (LiveCciValue.HasValue)
            cciPart = $"CCI={LiveCciValue.Value:F2}";
        else if (Result.CciValue.HasValue)
            cciPart = $"CCI={Result.CciValue.Value:F2}";
        else
            cciPart = "CCI=N/A";

        string ema20Part;
        if (Ema20Value > 0)
            ema20Part = $"EMA20={Ema20Value:F2} ({Ema20Signal})";
        else if (Result.Ema20Value.HasValue)
            ema20Part = $"EMA20={Result.Ema20Value.Value:F2} ({Result.Ema20Signal ?? "N/A"})";
        else
            ema20Part = "EMA20=N/A";

        var baseReason = Result.Reason ?? string.Empty;
        var idxMon = baseReason.IndexOf("Monitoring:", StringComparison.OrdinalIgnoreCase);
        if (idxMon >= 0)
            baseReason = baseReason[..idxMon].TrimEnd();

        var monitoringPart = $"Monitoring: {macdPart}; {rsiPart}; {cciPart}; {ema20Part}";
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

    private void UpdateLiveCciDisplay(string symbol, double cci)
    {
        if (SelectedSymbol?.Symbol == symbol)
        {
            SelectedSymbol.CciValue = cci;
            // CCI signal logic can be added here if needed
            // For now, just update the value
        }
    }

    private void UpdateEma20DisplayFromLive(string symbol, double ema20, decimal currentPrice)
    {
        if (SelectedSymbol?.Symbol != symbol)
            return;

        Ema20Value = (decimal)ema20;
        IsEma20AbovePrice = currentPrice > (decimal)ema20;
        Ema20Signal = IsEma20AbovePrice ? "Buy" : "Hold";

        // Keep Reason in sync with the same values shown in the indicator
        UpdateReasonWithIndicatorValues();
    }

    private void UpdateEma20Display()
    {
        if (Result?.Ema20Value == null)
            return;

        Ema20Value = (decimal)Result.Ema20Value;
        Ema20Signal = Result.Ema20Signal ?? "Hold";

        if (Result.Price.HasValue)
        {
            IsEma20AbovePrice = Result.Price.Value > Result.Ema20Value.Value;
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

    private async Task InitializeCciStateAsync()
    {
        if (_cciSettingsService == null || _candlestickStorage == null ||
            _config == null || SelectedSymbol == null)
        {
            _logger.LogDebug("AlgoRunnerViewModel: Skipping CCI state initialization - required services not available");
            return;
        }

        try
        {
            var settings = await _cciSettingsService.GetAsync();
            var interval = GetIntervalString(_config.IntervalSeconds);

            // Get historical candlesticks from storage
            var candlesticks = _candlestickStorage.GetCandlesticks(SelectedSymbol.Symbol, interval, int.MaxValue)
                .OrderBy(c => c.Timestamp)
                .ToList();

            if (candlesticks.Count >= settings.Period)
            {
                // Initialize CciEngine from historical warmup once
                _cciEngine?.Initialize(SelectedSymbol.Symbol, interval, candlesticks, settings.Period);

                _logger.LogInformation("AlgoRunnerViewModel: Initialized CCI state for {Symbol} with {Count} candlesticks",
                    SelectedSymbol.Symbol, candlesticks.Count);
            }
            else
            {
                _logger.LogWarning("AlgoRunnerViewModel: Insufficient candlesticks for CCI initialization. Need {Required}, got {Actual}",
                    settings.Period, candlesticks.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AlgoRunnerViewModel: Error initializing CCI state for {Symbol}", SelectedSymbol?.Symbol);
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

        // Calculate P/L if we have a position (open or closed) OR if position was just closed
        if ((HasPosition || PositionClosed) && EntryPrice.HasValue && EntryPrice.Value > 0)
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

    /// <summary>
    /// Closes the current open position for this algorithm runner.
    /// Sets exit price to current price, updates position state, and persists to database.
    /// </summary>
    public async Task ClosePositionAsync()
    {
        // Only close if we have an open position
        if (!HasPosition || PositionClosed || SelectedSymbol == null || !EntryPrice.HasValue)
        {
            _logger.LogInformation("No open position to close for {Symbol}", SelectedSymbol?.Symbol ?? "Unknown");
            return;
        }

        try
        {
            _logger.LogInformation("Closing position for {Symbol}", SelectedSymbol.Symbol);

            // Get current price
            var currentPrice = (decimal)SelectedSymbol.LastPrice;

            // Update position state
            ExitPrice = currentPrice;
            PositionClosed = true;
            HasPosition = false;

            // Update P/L calculations
            UpdateProfitLoss();

            // Update trade in database if we have a trade ID
            if (_currentTradeId.HasValue)
            {
                await UpdateTradeAsync();
            }
            else
            {
                _logger.LogWarning("No trade ID found for {Symbol} - position closed but trade not saved in database", SelectedSymbol.Symbol);
            }

            _logger.LogInformation("Position closed for {Symbol} at {Price:C2}", SelectedSymbol.Symbol, currentPrice);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing position for {Symbol}", SelectedSymbol?.Symbol);
            // Don't throw - allow operation to continue even if position closure fails
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

