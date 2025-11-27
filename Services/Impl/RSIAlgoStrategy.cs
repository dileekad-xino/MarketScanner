using IBApi;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Linq;

namespace MarketScanner.Services.Impl;

/// <summary>
/// RSI-based trading algorithm strategy.
/// Uses Relative Strength Index (RSI) to generate buy/sell/hold signals.
/// Enhanced with stateful 50→60/70 swing exit logic.
/// </summary>
public class RSIAlgoStrategy : IAlgoStrategy
{
    private readonly IbkrGatewayService _ibkrService;
    private readonly IRsiSettingsService _settingsService;
    private readonly ILogger<RSIAlgoStrategy> _logger;
    
    private const int TrendEmaPeriod = 20;
    
    // Stateful tracking: per-symbol trade contexts for 50-cross BUY entries
    // Key: "symbol|timeframe" (e.g., "AAPL|1 min")
    private static readonly ConcurrentDictionary<string, RsiTradeContext> _activeTradeContexts = new();

    public string Name => "RSI Strategy";
    public string Description => "Trading strategy based on Relative Strength Index (RSI). " +
                               "Buy when RSI < 30 (oversold), Sell when RSI > 70 (overbought). " +
                               "Enhanced with 50→60/70 swing exit logic.";

    public RSIAlgoStrategy(
        IbkrGatewayService ibkrService,
        IRsiSettingsService settingsService,
        ILogger<RSIAlgoStrategy> logger)
    {
        _ibkrService = ibkrService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("RSIAlgoStrategy: Executing for symbol {Symbol}", symbol.Symbol);

            var settings = await _settingsService.GetAsync(ct).ConfigureAwait(false);

            // Step 1: Get historical bars from IBKR
            IReadOnlyList<Bar> bars;
            try
            {
                bars = await _ibkrService.GetHistoricalBarsForRSIAsync(
                    symbol.Symbol,
                    days: settings.HistoricalDays,
                    barSize: settings.BarSize,
                    ct: ct);

                if (bars == null || bars.Count == 0)
                {
                    return new AlgoResult(
                        Symbol: symbol.Symbol,
                        Action: AlgoAction.Hold,
                        Price: symbol.LastPrice,
                        Reason: "No historical data available for RSI calculation",
                        Timestamp: DateTime.UtcNow,
                        RsiValue: null,
                        RsiSignal: "NEUTRAL"
                    );
                }

                _logger.LogInformation("RSIAlgoStrategy: Received {Count} historical bars for {Symbol}", bars.Count, symbol.Symbol);
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "RSIAlgoStrategy: Historical data request timed out for {Symbol}", symbol.Symbol);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Historical data request timed out: {ex.Message}",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RSIAlgoStrategy: Failed to get historical data for {Symbol}", symbol.Symbol);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Error retrieving historical data: {ex.Message}",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }

            // Step 2: Extract close prices from bars
            var closePrices = bars.Select(b => (double)b.Close).ToArray();

            if (closePrices.Length < settings.Period + 1)
            {
                _logger.LogWarning("RSIAlgoStrategy: Insufficient data for {Symbol}. Need {Required} bars, got {Actual}", 
                    symbol.Symbol, settings.Period + 1, closePrices.Length);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Insufficient historical data: need {settings.Period + 1} bars, got {closePrices.Length}",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }

            // Step 3: Calculate RSI series
            double[] rsiSeries;
            try
            {
                rsiSeries = RSICalculator.CalculateSeries(closePrices, settings.Period);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RSIAlgoStrategy: Error calculating RSI for {Symbol}", symbol.Symbol);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Error calculating RSI: {ex.Message}",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }

            var usableRsi = rsiSeries.Skip(settings.Period - 1).ToArray();
            if (usableRsi.Length < 2)
            {
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: "Not enough RSI samples for crossover detection",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: usableRsi.LastOrDefault(),
                    RsiSignal: "NEUTRAL"
                );
            }

            double prevRsi = usableRsi[^2];
            double currentRsi = usableRsi[^1];
            double ema = MovingAverage.CalculateEma(closePrices, TrendEmaPeriod);
            bool uptrend = closePrices[^1] >= ema;
            bool downtrend = !uptrend;

            // Generate context key for tracking active trades
            string contextKey = GetContextKey(symbol.Symbol, settings.BarSize);
            
            // Check if there's an active trade context for this symbol
            _activeTradeContexts.TryGetValue(contextKey, out var activeContext);

            var (action, signal, reason, shouldCloseContext, shouldOpenContext) = EvaluateSignal(
                symbol, settings, prevRsi, currentRsi, uptrend, downtrend, ema, activeContext, usableRsi.Length - 1);

            // State management: Open or close trade context based on signal
            if (shouldCloseContext && activeContext != null)
            {
                _activeTradeContexts.TryRemove(contextKey, out _);
                _logger.LogInformation("RSIAlgoStrategy: Closed 50-cross BUY context for {Symbol} (timeframe={Timeframe}, entryRsi={EntryRsi:F2}, peakRsi={PeakRsi:F2})",
                    symbol.Symbol, settings.BarSize, activeContext.EntryRsi, activeContext.PeakRsi);
            }

            if (shouldOpenContext && action == AlgoAction.Buy && signal.Contains("50"))
            {
                var newContext = new RsiTradeContext(
                    symbol.Symbol,
                    settings.BarSize,
                    DateTime.UtcNow,
                    currentRsi,
                    usableRsi.Length - 1);
                
                _activeTradeContexts[contextKey] = newContext;
                _logger.LogInformation("RSIAlgoStrategy: Opened 50-cross BUY context for {Symbol} (timeframe={Timeframe}, rsi={Rsi:F2}, bar={BarIndex})",
                    symbol.Symbol, settings.BarSize, currentRsi, newContext.EntryBarIndex);
            }

            // Update active context peak tracking if context still active
            if (_activeTradeContexts.TryGetValue(contextKey, out var updatedContext))
            {
                updatedContext.UpdatePeak(currentRsi, settings.TakeProfitLevel, settings.Overbought);
            }

            _logger.LogInformation("RSIAlgoStrategy: {Action} signal for {Symbol} - RSI={RSI:F2}, Signal={Signal}, ActiveContext={HasContext}", 
                action, symbol.Symbol, currentRsi, signal, activeContext != null);

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: action,
                Price: symbol.LastPrice,
                Reason: reason,
                Timestamp: DateTime.UtcNow,
                RsiValue: currentRsi,
                RsiSignal: signal
            );
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("RSIAlgoStrategy: Execution cancelled for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: "Algorithm execution was cancelled",
                Timestamp: DateTime.UtcNow,
                RsiValue: null,
                RsiSignal: "NEUTRAL"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RSIAlgoStrategy: Unexpected error executing algorithm for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"Unexpected error: {ex.Message}",
                Timestamp: DateTime.UtcNow,
                RsiValue: null,
                RsiSignal: "NEUTRAL"
            );
        }
    }

    /// <summary>
    /// Generates a unique context key for tracking active trades per symbol and timeframe.
    /// </summary>
    private static string GetContextKey(string symbol, string timeframe) => $"{symbol}|{timeframe}";

    /// <summary>
    /// Evaluates RSI signal logic with enhanced 50→60/70 swing exit tracking.
    /// Returns: (Action, Signal, Reason, ShouldCloseContext, ShouldOpenContext)
    /// </summary>
    private (AlgoAction Action, string Signal, string Reason, bool ShouldCloseContext, bool ShouldOpenContext) EvaluateSignal(
        ScannerRowViewModel symbol,
        RsiSettings settings,
        double prevRsi,
        double currentRsi,
        bool uptrend,
        bool downtrend,
        double ema,
        RsiTradeContext? activeContext,
        int currentBarIndex)
    {
        string priceInfo = $"Price ${symbol.LastPrice:F2} vs EMA{TrendEmaPeriod} {ema:F2}";

        // ========== NEW: Check for swing exit conditions if we have an active 50-cross BUY context ==========
        if (activeContext != null)
        {
            // Priority 1: Check for 70-rejection (strong rally exit) - this takes precedence
            if (activeContext.ReachedOverbought && prevRsi >= settings.Overbought && currentRsi < settings.Overbought)
            {
                return (AlgoAction.Sell,
                    "STRONG SELL",
                    $"RSI reached {activeContext.PeakRsi:F1} after prior 50-cross BUY and then fell back below {settings.Overbought} -> STRONG SELL (overbought rejection). {priceInfo}",
                    ShouldCloseContext: true,
                    ShouldOpenContext: false);
            }

            // Priority 2: Check for 60-rejection (mild rally exit) - only if 70-rejection didn't trigger
            if (activeContext.ReachedTakeProfitLevel && prevRsi >= settings.TakeProfitLevel && currentRsi < settings.TakeProfitLevel)
            {
                // Don't trigger if we're also crossing below overbought on same bar (70-rejection takes priority)
                if (!(prevRsi >= settings.Overbought && currentRsi < settings.Overbought))
                {
                    return (AlgoAction.Sell,
                        "SELL",
                        $"RSI reached {activeContext.PeakRsi:F1} after prior 50-cross BUY and then fell back below {settings.TakeProfitLevel} -> SELL (mild exit). {priceInfo}",
                        ShouldCloseContext: true,
                        ShouldOpenContext: false);
                }
            }
        }

        // ========== EXISTING: Traditional RSI signals (with potential context state changes) ==========

        // STRONG BUY: RSI rebounded above oversold (momentum reversal)
        if (prevRsi <= settings.Oversold && currentRsi > settings.Oversold)
        {
            // Close any existing context since we have a new strong signal
            return (AlgoAction.Buy,
                "STRONG BUY",
                $"RSI rebounded above oversold ({settings.Oversold}) -> STRONG BUY. {priceInfo}",
                ShouldCloseContext: activeContext != null,
                ShouldOpenContext: false);
        }

        // STRONG BUY: RSI is deeply oversold (absolute level trigger)
        if (currentRsi <= settings.Oversold)
        {
            return (AlgoAction.Buy,
                "STRONG BUY",
                $"RSI {currentRsi:F1} is deeply oversold (below {settings.Oversold}) -> STRONG BUY. {priceInfo}",
                ShouldCloseContext: activeContext != null,
                ShouldOpenContext: false);
        }

        // BUY: RSI crossed above 50 with uptrend (momentum confirmation) - THIS OPENS A NEW CONTEXT
        if (prevRsi < 50 && currentRsi >= 50 && uptrend)
        {
            return (AlgoAction.Buy,
                "BUY",
                $"RSI crossed above 50 (uptrend) on bar {currentBarIndex} -> BUY. {priceInfo}",
                ShouldCloseContext: activeContext != null, // Close old context if exists
                ShouldOpenContext: true); // Open new 50-cross context
        }

        // STRONG SELL: RSI rejected from overbought (momentum reversal)
        // This can trigger even without an active context (pure mean-reversion)
        if (prevRsi >= settings.Overbought && currentRsi < settings.Overbought)
        {
            return (AlgoAction.Sell,
                "STRONG SELL",
                $"RSI rejected overbought ({settings.Overbought}) -> STRONG SELL. {priceInfo}",
                ShouldCloseContext: activeContext != null,
                ShouldOpenContext: false);
        }

        // STRONG SELL: RSI is deeply overbought (absolute level trigger)
        if (currentRsi >= settings.Overbought)
        {
            return (AlgoAction.Sell,
                "STRONG SELL",
                $"RSI {currentRsi:F1} is deeply overbought (above {settings.Overbought}) -> STRONG SELL. {priceInfo}",
                ShouldCloseContext: activeContext != null,
                ShouldOpenContext: false);
        }

        // SELL: RSI crossed below 50 with downtrend (momentum confirmation)
        if (prevRsi > 50 && currentRsi <= 50 && downtrend)
        {
            return (AlgoAction.Sell,
                "SELL",
                $"RSI crossed below 50 with price below EMA -> SELL. {priceInfo}",
                ShouldCloseContext: activeContext != null,
                ShouldOpenContext: false);
        }

        // NEUTRAL: No signal triggered
        var signal = uptrend ? "NEUTRAL (UPTREND)" : "NEUTRAL";
        return (AlgoAction.Hold,
            signal,
            $"RSI {currentRsi:F1} is neutral between levels. {priceInfo}",
            ShouldCloseContext: false,
            ShouldOpenContext: false);
    }
}

