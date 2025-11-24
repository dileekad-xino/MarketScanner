using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Trading strategy based on MACD (Moving Average Convergence Divergence) indicator.
/// Uses real-time candlesticks to calculate MACD and generate buy/sell signals.
/// </summary>
public class MacdStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ILogger<MacdStrategy> _logger;

    public string Name => "MACD Strategy";
    public string Description => "Uses MACD crossover and histogram signals from real-time candlesticks for trading decisions.";

    public MacdStrategy(
        ICandlestickStorage candlestickStorage,
        CandlestickConfig config,
        ILogger<MacdStrategy> logger)
    {
        _candlestickStorage = candlestickStorage;
        _config = config;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            var interval = GetIntervalString(_config.IntervalSeconds);
            
            // Get recent candlesticks (need at least slowPeriod + signalPeriod for MACD)
            var minRequired = _config.Macd.SlowPeriod + _config.Macd.SignalPeriod;
            var candlesticks = _candlestickStorage.GetCandlesticks(symbol.Symbol, interval, minRequired + 10);

            if (candlesticks.Count < minRequired)
            {
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Insufficient candlesticks for MACD: need {minRequired}, got {candlesticks.Count}",
                    Timestamp: DateTime.UtcNow
                );
            }

            // Extract closing prices in chronological order
            var closes = candlesticks.Select(c => c.Close).ToList();

            // Calculate current MACD
            var macd = TechnicalIndicators.CalculateMacd(
                closes,
                _config.Macd.FastPeriod,
                _config.Macd.SlowPeriod,
                _config.Macd.SignalPeriod);

            if (macd == null)
            {
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: "MACD calculation failed",
                    Timestamp: DateTime.UtcNow
                );
            }

            // Update MACD with symbol and interval
            macd = macd with { Symbol = symbol.Symbol, Interval = interval };

            // Get previous MACD for crossover detection (exclude last close)
            var prevCloses = closes.Take(closes.Count - 1).ToList();
            var prevMacd = TechnicalIndicators.CalculateMacd(
                prevCloses,
                _config.Macd.FastPeriod,
                _config.Macd.SlowPeriod,
                _config.Macd.SignalPeriod);

            var action = AlgoAction.Hold;
            var reason = "";

            // Bullish signals
            if (macd.MacdLine > macd.SignalLine &&
                prevMacd != null &&
                prevMacd.MacdLine <= prevMacd.SignalLine)
            {
                // Bullish crossover: MACD crossed above signal
                action = AlgoAction.Buy;
                reason = $"MACD bullish crossover: MACD ({macd.MacdLine:F4}) crossed above Signal ({macd.SignalLine:F4})";
            }
            else if (macd.MacdLine > macd.SignalLine && macd.HasPositiveHistogram)
            {
                // MACD above signal with positive histogram
                action = AlgoAction.Buy;
                reason = $"MACD above signal with positive histogram: {macd.Histogram:F4}";
            }
            // Bearish signals
            else if (macd.MacdLine < macd.SignalLine &&
                     prevMacd != null &&
                     prevMacd.MacdLine >= prevMacd.SignalLine)
            {
                // Bearish crossover: MACD crossed below signal
                action = AlgoAction.Sell;
                reason = $"MACD bearish crossover: MACD ({macd.MacdLine:F4}) crossed below Signal ({macd.SignalLine:F4})";
            }
            else if (macd.MacdLine < macd.SignalLine && macd.HasNegativeHistogram)
            {
                // MACD below signal with negative histogram
                action = AlgoAction.Sell;
                reason = $"MACD below signal with negative histogram: {macd.Histogram:F4}";
            }
            else
            {
                reason = $"MACD neutral: MACD={macd.MacdLine:F4}, Signal={macd.SignalLine:F4}, Histogram={macd.Histogram:F4}";
            }

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: action,
                Price: symbol.LastPrice,
                Reason: reason,
                Timestamp: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MACD strategy failed for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"MACD strategy error: {ex.Message}",
                Timestamp: DateTime.UtcNow
            );
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
}

