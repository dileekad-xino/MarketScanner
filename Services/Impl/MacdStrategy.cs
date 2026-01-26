using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

public class MacdStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ILogger<MacdStrategy> _logger;
    private readonly MacdEngine _macdEngine;

    public string Name => "MACD Strategy";
    public string Description => "TradingView-style MACD histogram momentum signals optimized for intraday scalping.";

    public MacdStrategy(
        ICandlestickStorage storage,
        CandlestickConfig config,
        ILogger<MacdStrategy> logger,
        MacdEngine engine)
    {
        _candlestickStorage = storage;
        _config = config;
        _logger = logger;
        _macdEngine = engine;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            var interval = GetIntervalString(_config.IntervalSeconds);
            // Live-only: initialize once from historical warmup (if needed), then read tick-updated engine state
            if (!_macdEngine.TryGetState(symbol.Symbol, interval, out _))
            {
                var candles = _candlestickStorage
                    .GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                _logger.LogInformation("Initializing MACD engine for {Symbol} (warm-up from {Count} candles)", symbol.Symbol, candles.Count);
                _macdEngine.Initialize(
                    symbol.Symbol,
                    interval,
                    candles,
                    _config.Macd.FastPeriod,
                    _config.Macd.SlowPeriod,
                    _config.Macd.SignalPeriod);
            }

            var result = _macdEngine.GetLastMacd(symbol.Symbol, interval);
            if (result == null)
                return Hold(symbol, "MACD unavailable (engine not initialized yet)");

            var (macd, signal, hist) = result.Value;

            var macdData = new MacdData(
                symbol.Symbol,
                (decimal)macd,
                (decimal)signal,
                (decimal)hist,
                DateTime.UtcNow,
                interval
            );

            var (action, reason, cross) = DetectSignals(macdData);

            return new AlgoResult(
                symbol.Symbol,
                action,
                symbol.LastPrice,
                reason,
                DateTime.UtcNow,
                macdData,
                cross
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MACD failed for {Symbol}", symbol.Symbol);
            return Hold(symbol, $"MACD error: {ex.Message}");
        }
    }


    private (AlgoAction, string, CrossoverStatus) DetectSignals(MacdData m)
    {
        const decimal EPS = 0.000001m;

        // Get previous histogram using preferred method, fallback to calculation
        decimal? prevHist = null;
        var prevWithHist = _macdEngine.GetPreviousMacdWithHist(m.Symbol, m.Interval);
        
        if (prevWithHist != null)
        {
            prevHist = (decimal)prevWithHist.Value.Hist;
        }
        else
        {
            // Fallback: calculate from previous MACD/Signal
            var prev = _macdEngine.GetPreviousMacd(m.Symbol, m.Interval);
            if (prev != null)
            {
                prevHist = (decimal)prev.Value.Macd - (decimal)prev.Value.Signal;
            }
        }

        // If no previous histogram available, use basic MACD crossover logic as fallback
        if (!prevHist.HasValue)
        {
            bool aboveFallback = m.MacdLine > m.SignalLine + EPS;
            bool belowFallback = m.MacdLine < m.SignalLine - EPS;

            if (aboveFallback && m.MacdLine > 0)
                return (AlgoAction.Buy, $"MACD bullish (no hist history): MACD={m.MacdLine:F4}", CrossoverStatus.None);
            if (belowFallback && m.MacdLine < 0)
                return (AlgoAction.Sell, $"MACD bearish (no hist history): MACD={m.MacdLine:F4}", CrossoverStatus.None);

            return (AlgoAction.Hold, $"MACD waiting for history: MACD={m.MacdLine:F4}", CrossoverStatus.None);
        }

        // Histogram-first signal generation (TradingView-style)
        decimal hist = m.Histogram;
        decimal histChange = hist - prevHist.Value;
        decimal threshold = GetHistogramThreshold(m.Interval);

        bool above = m.MacdLine > m.SignalLine + EPS;
        bool below = m.MacdLine < m.SignalLine - EPS;

        // Rule 1: hist >= 0 && hist > hist[1] + threshold → BUY (bullish momentum increasing)
        if (hist >= 0 && histChange > threshold)
        {
            // Additional confirmation: MACD line should be above signal
            if (above)
            {
                return (AlgoAction.Buy,
                    $"Histogram bullish momentum: Hist={hist:F4} (↑{histChange:+0.0000}), MACD={m.MacdLine:F4}",
                    CrossoverStatus.None);
            }
        }

        // Rule 2: hist >= 0 && hist <= hist[1] + threshold → HOLD (bullish momentum weakening)
        if (hist >= 0 && histChange <= threshold)
        {
            // If MACD still above signal, hold; if crossed below, exit
            if (above)
            {
                return (AlgoAction.Sell,
                    $"Histogram momentum weakening: Hist={hist:F4} (↓{histChange:0.0000}), MACD={m.MacdLine:F4}",
                    CrossoverStatus.None);
            }
            else
            {
                // Bearish crossover while histogram positive → exit signal
                return (AlgoAction.Sell,
                    $"Histogram weakening + bearish crossover: Hist={hist:F4}, MACD={m.MacdLine:F4}",
                    CrossoverStatus.CrossedDown);
            }
        }

        // Rule 3: hist < 0 && hist > hist[1] + threshold → SELL (exit longs, bearish weakening)
        if (hist < 0 && histChange > threshold)
        {
            // Histogram improving but still negative - exit long positions
            return (AlgoAction.Sell,
                $"Histogram improving but negative: Hist={hist:F4} (↑{histChange:+0.0000}), MACD={m.MacdLine:F4}",
                CrossoverStatus.None);
        }

        // Rule 4: hist < 0 && hist <= hist[1] + threshold → SELL (bearish momentum increasing)
        if (hist < 0 && histChange <= threshold)
        {
            return (AlgoAction.Sell,
                $"Histogram bearish momentum: Hist={hist:F4} (↓{histChange:0.0000}), MACD={m.MacdLine:F4}",
                CrossoverStatus.None);
        }

        // Fallback: HOLD
        return (AlgoAction.Hold,
            $"MACD stable: Hist={hist:F4}, MACD={m.MacdLine:F4}, Signal={m.SignalLine:F4}",
            CrossoverStatus.None);
    }

    /// <summary>
    /// Evaluate MACD signals from a supplied MacdData snapshot (live or candle).
    /// </summary>
    public (AlgoAction action, string reason, CrossoverStatus cross) EvaluateFromMacdData(MacdData macdData)
    {
        var (a, r, c) = DetectSignals(macdData);
        return (a, r, c);
    }

    private AlgoResult Hold(ScannerRowViewModel s, string reason) =>
        new(s.Symbol, AlgoAction.Hold, s.LastPrice, reason, DateTime.UtcNow);

    private string GetIntervalString(int s) =>
        s switch
        {
            15 => "15s",
            30 => "30s",
            60 => "1min",
            _ => $"{s}s"
        };

    /// <summary>
    /// Gets minimum histogram change threshold based on timeframe to reduce noise.
    /// </summary>
    private decimal GetHistogramThreshold(string interval)
    {
        return interval switch
        {
            "15s" => 0.001m,   // Very small threshold for 15s scalping
            "30s" => 0.002m,   // Small threshold for 30s scalping
            "1min" => 0.003m,  // Standard threshold for 1m scalping
            _ => 0.002m        // Default for other intervals
        };
    }
}