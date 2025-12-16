using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace MarketScanner.Services.Impl;

/// <summary>
/// RSI-based trading algorithm strategy.
/// Uses Relative Strength Index (RSI) to generate buy/sell/hold signals.
/// Uses real-time candlesticks from CandlestickStorage for continuous updates.
/// </summary>
public class RSIAlgoStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly IRsiSettingsService _settingsService;
    private readonly ITradeService? _tradeService;
    private readonly ILogger<RSIAlgoStrategy> _logger;
    
    private const int TrendEmaPeriod = 20;

    public string Name => "RSI Strategy";
    public string Description => "Trading strategy based on Relative Strength Index (RSI). " +
                               "Buy when RSI crosses above 50 with uptrend. Uses trailing stop for position management.";

    public RSIAlgoStrategy(
        ICandlestickStorage candlestickStorage,
        CandlestickConfig config,
        IRsiSettingsService settingsService,
        ILogger<RSIAlgoStrategy> logger,
        ITradeService? tradeService = null)
    {
        _candlestickStorage = candlestickStorage;
        _config = config;
        _settingsService = settingsService;
        _tradeService = tradeService;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("RSIAlgoStrategy: Executing for symbol {Symbol}", symbol.Symbol);

            var settings = await _settingsService.GetAsync(ct).ConfigureAwait(false);

            // Step 1: Get candlesticks from storage (same pattern as MACD)
            var interval = GetIntervalString(_config.IntervalSeconds);
            var minRequired = settings.Period + 1; // RSI needs period + 1 data points
            
            // Get all available candlesticks from storage
            var candlesticks = _candlestickStorage.GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                .OrderBy(c => c.Timestamp)
                .ToList();

            if (candlesticks == null || candlesticks.Count == 0)
            {
                _logger.LogWarning("RSIAlgoStrategy: No candlesticks available for {Symbol} (interval={Interval})", symbol.Symbol, interval);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"No candlestick data available for RSI calculation (interval: {interval})",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }

            _logger.LogInformation("RSIAlgoStrategy: Retrieved {Count} candlesticks for {Symbol} (interval={Interval})", 
                candlesticks.Count, symbol.Symbol, interval);

            // Step 2: Extract close prices from candlesticks
            var closePrices = candlesticks.Select(c => (double)c.Close).ToArray();

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

            // Step 3: Calculate RSI series from candlesticks
            double[] rsiSeries;
            try
            {
                rsiSeries = RSICalculator.CalculateSeriesFromCandlesticks(candlesticks, settings.Period);
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

            var currentPrice = (decimal)symbol.LastPrice;
            
            // Check initial stop-loss first (if we have an open position)
            var initialStopResult = await CheckInitialStopLossAsync(symbol.Symbol, currentPrice, settings, ct);
            if (initialStopResult != null)
            {
                _logger.LogInformation("RSIAlgoStrategy: Initial stop-loss triggered for {Symbol} - Price={Price:F2}, Stop={Stop:F2}", 
                    symbol.Symbol, currentPrice, initialStopResult.Value);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Sell,
                    Price: symbol.LastPrice,
                    Reason: $"Initial stop-loss triggered: Price ${currentPrice:F2} <= Stop ${initialStopResult.Value:F2}",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: currentRsi,
                    RsiSignal: "STOP-LOSS SELL"
                );
            }
            
            // Check for trailing stop if we have an open position (price-based)
            var trailingStopResult = await CheckTrailingStopAsync(symbol.Symbol, currentPrice, settings, ct);
            if (trailingStopResult != null)
            {
                var (highestPrice, trailingStopReason) = trailingStopResult.Value;
                _logger.LogInformation("RSIAlgoStrategy: Trailing stop triggered for {Symbol} - Price={Price:F2}, Highest={Highest:F2}, Mode={Mode}, Distance={Distance}", 
                    symbol.Symbol, currentPrice, highestPrice, settings.TrailingStopMode, settings.TrailingStopDistance);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Sell,
                    Price: symbol.LastPrice,
                    Reason: trailingStopReason,
                    Timestamp: DateTime.UtcNow,
                    RsiValue: currentRsi,
                    RsiSignal: "TRAILING STOP SELL"
                );
            }

            var (action, signal, reason) = EvaluateSignal(symbol, settings, prevRsi, currentRsi, uptrend, downtrend, ema);

            _logger.LogInformation("RSIAlgoStrategy: {Action} signal for {Symbol} - RSI={RSI:F2}, Signal={Signal}", 
                action, symbol.Symbol, currentRsi, signal);

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

    private (AlgoAction Action, string Signal, string Reason) EvaluateSignal(
        ScannerRowViewModel symbol,
        RsiSettings settings,
        double prevRsi,
        double currentRsi,
        bool uptrend,
        bool downtrend,
        double ema)
    {
        string priceInfo = $"Price ${symbol.LastPrice:F2} vs EMA{TrendEmaPeriod} {ema:F2}";

        // STRONG BUY signals removed for intraday trading - oversold conditions are too risky
        // Commented out: RSI rebounded above oversold (momentum reversal)
        // if (prevRsi <= settings.Oversold && currentRsi > settings.Oversold)
        // {
        //     return (AlgoAction.Buy,
        //         "STRONG BUY",
        //         $"RSI rebounded above oversold ({settings.Oversold}) -> STRONG BUY. {priceInfo}");
        // }

        // Commented out: RSI is deeply oversold (absolute level trigger)
        // if (currentRsi <= settings.Oversold)
        // {
        //     return (AlgoAction.Buy,
        //         "STRONG BUY",
        //         $"RSI {currentRsi:F1} is deeply oversold (below {settings.Oversold}) -> STRONG BUY. {priceInfo}");
        // }

        // BUY: RSI crossed above 50 with uptrend (momentum confirmation)
        if (prevRsi < 50 && currentRsi >= 50 && uptrend)
        {
            return (AlgoAction.Buy,
                "BUY",
                $"RSI crossed above 50 with price above EMA -> BUY. {priceInfo}");
        }

        // STRONG SELL: RSI rejected from overbought (momentum reversal)
        if (prevRsi >= settings.Overbought && currentRsi < settings.Overbought)
        {
            return (AlgoAction.Sell,
                "STRONG SELL",
                $"RSI rejected overbought ({settings.Overbought}) -> STRONG SELL. {priceInfo}");
        }

        // STRONG SELL: RSI is deeply overbought (absolute level trigger)
        if (currentRsi >= settings.Overbought)
        {
            return (AlgoAction.Sell,
                "STRONG SELL",
                $"RSI {currentRsi:F1} is deeply overbought (above {settings.Overbought}) -> STRONG SELL. {priceInfo}");
        }

        // SELL: RSI crossed below 50 with downtrend (momentum confirmation)
        if (prevRsi > 50 && currentRsi <= 50 && downtrend)
        {
            return (AlgoAction.Sell,
                "SELL",
                $"RSI crossed below 50 with price below EMA -> SELL. {priceInfo}");
        }

        var signal = uptrend ? "NEUTRAL (UPTREND)" : "NEUTRAL";
        return (AlgoAction.Hold,
            signal,
            $"RSI {currentRsi:F1} is neutral between levels. {priceInfo}");
    }

    private async Task<decimal?> CheckInitialStopLossAsync(
        string symbol,
        decimal currentPrice,
        RsiSettings settings,
        CancellationToken ct)
    {
        if (_tradeService == null)
        {
            return null; // No trade service available
        }

        try
        {
            var openTrades = await _tradeService.GetOpenTradesAsync();
            var openTrade = openTrades.FirstOrDefault(t => 
                t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && 
                t.AlgorithmName == Name);

            if (openTrade == null || !openTrade.InitialStopLossPrice.HasValue)
            {
                return null; // No open position or no initial stop-loss set
            }

            // Check if initial stop-loss is triggered
            if (currentPrice <= openTrade.InitialStopLossPrice.Value)
            {
                return openTrade.InitialStopLossPrice.Value;
            }

            return null; // Stop-loss not triggered
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking initial stop-loss for {Symbol}", symbol);
            return null; // Fail gracefully
        }
    }

    private async Task<(decimal HighestPrice, string Reason)?> CheckTrailingStopAsync(
        string symbol,
        decimal currentPrice,
        RsiSettings settings,
        CancellationToken ct)
    {
        if (_tradeService == null)
        {
            return null; // No trade service available, skip trailing stop check
        }

        try
        {
            var openTrades = await _tradeService.GetOpenTradesAsync();
            var openTrade = openTrades.FirstOrDefault(t => 
                t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && 
                t.AlgorithmName == Name);

            if (openTrade == null)
            {
                return null; // No open position for this symbol
            }

            // Check if trailing stop is activated (price reached activation level)
            if (!openTrade.TrailingStopActivated && openTrade.TrailingStopActivationPrice.HasValue)
            {
                if (currentPrice >= openTrade.TrailingStopActivationPrice.Value)
                {
                    // Activate trailing stop
                    openTrade.TrailingStopActivated = true;
                    openTrade.HighestPrice = currentPrice; // Initialize highest price
                    await _tradeService.UpdateTradeAsync(openTrade);
                    _logger.LogInformation("Trailing stop activated for {Symbol} at price {Price:F2}", symbol, currentPrice);
                }
                else
                {
                    // Not activated yet - waiting for price to reach activation level
                    return null;
                }
            }

            // If trailing stop not activated, don't check it
            if (!openTrade.TrailingStopActivated)
            {
                return null;
            }

            // Get highest price reached (or use entry price if null)
            var highestPrice = openTrade.HighestPrice ?? openTrade.EntryPrice;

            // FIX RACE CONDITION: Update highest price immediately if current price is higher
            if (currentPrice > highestPrice)
            {
                openTrade.HighestPrice = currentPrice;
                await _tradeService.UpdateTradeAsync(openTrade);
                // Return null to let normal signal evaluation proceed
                return null;
            }

            // Calculate trailing stop level based on mode
            decimal stopPrice;
            if (settings.TrailingStopMode == TrailingStopMode.Percentage)
            {
                // Percentage mode: stopPrice = highestPrice * (1 - distance/100)
                stopPrice = highestPrice * (1 - (decimal)(settings.TrailingStopDistance / 100.0));
            }
            else // Price mode
            {
                // Price mode: stopPrice = highestPrice - distance
                stopPrice = highestPrice - (decimal)settings.TrailingStopDistance;
            }

            // Never move stop backward - ensure stop only moves up
            var previousStopPrice = openTrade.TrailingStopPrice;
            if (previousStopPrice.HasValue && stopPrice < previousStopPrice.Value)
            {
                stopPrice = previousStopPrice.Value;
            }
            
            // Update trailing stop price in trade (only if it changed)
            if (!openTrade.TrailingStopPrice.HasValue || openTrade.TrailingStopPrice.Value != stopPrice)
            {
                openTrade.TrailingStopPrice = stopPrice;
                await _tradeService.UpdateTradeAsync(openTrade);
            }

            // Check if trailing stop is triggered
            if (currentPrice <= stopPrice)
            {
                var modeStr = settings.TrailingStopMode == TrailingStopMode.Percentage ? "%" : "$";
                var distanceStr = settings.TrailingStopMode == TrailingStopMode.Percentage 
                    ? $"{settings.TrailingStopDistance:F2}%" 
                    : $"${settings.TrailingStopDistance:F2}";
                
                return (highestPrice, 
                    $"Trailing stop triggered: Price dropped from ${highestPrice:F2} to ${currentPrice:F2} (stop: ${stopPrice:F2}, mode: {modeStr}, distance: {distanceStr})");
            }

            return null; // No trailing stop trigger
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking trailing stop for {Symbol}", symbol);
            return null; // Fail gracefully - don't block signal evaluation
        }
    }

    /// <summary>
    /// Converts interval seconds to interval string format used by CandlestickStorage.
    /// </summary>
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
}

