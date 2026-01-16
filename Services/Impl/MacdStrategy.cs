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
    public string Description => "Generates trading signals based on MACD crossovers.";

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

        bool above = m.MacdLine > m.SignalLine + EPS;
        bool below = m.MacdLine < m.SignalLine - EPS;

        var prev = _macdEngine.GetPreviousMacd(m.Symbol, m.Interval);

        // Calculate previous histogram (since GetPreviousMacd doesn't return it)
        decimal? prevHist = null;
        if (prev != null)
        {
            prevHist = (decimal)prev.Value.Macd - (decimal)prev.Value.Signal;
        }

        bool bullish = false;
        bool bearish = false;

        if (prev != null)
        {
            decimal prevMacd = (decimal)prev.Value.Macd;
            decimal prevSignal = (decimal)prev.Value.Signal;

            bullish = prevMacd <= prevSignal + EPS && above;
            bearish = prevMacd >= prevSignal - EPS && below;
        }

        // Histogram momentum analysis
        bool histGrowing = m.Histogram > 0 && prevHist.HasValue && m.Histogram > prevHist.Value;
        bool histFalling = prevHist.HasValue && m.Histogram < prevHist.Value;

        // Priority 1: Full bearish reversal (highest priority - exit immediately)
        if (bearish && m.MacdLine < 0)
        {
            return (AlgoAction.Sell,
                $"Bearish MACD reversal: MACD={m.MacdLine:F4}, Signal={m.SignalLine:F4}",
                CrossoverStatus.CrossedDown);
        }

        // Priority 2: Momentum failure exit (early exit signal)
        if (histFalling && m.MacdLine > 0)
        {
            return (AlgoAction.Sell,
                $"MACD momentum weakening (histogram contraction): Hist={m.Histogram:F4}, PrevHist={prevHist?.ToString("F4") ?? "N/A"}",
                CrossoverStatus.None);
        }

        // Priority 3: Bullish ignition (entry signal)
        if (bullish && m.MacdLine > -0.05m)
        {
            return (AlgoAction.Buy,
                $"Bullish MACD ignition: MACD={m.MacdLine:F4}, Signal={m.SignalLine:F4}, Hist={m.Histogram:F4}",
                CrossoverStatus.CrossedUp);
        }

        // Priority 4: Trend continuation (add-on signal)
        if (above && m.MacdLine > 0 && histGrowing)
        {
            return (AlgoAction.Buy,
                $"MACD continuation: momentum expanding (Hist={m.Histogram:F4}, PrevHist={prevHist?.ToString("F4") ?? "N/A"})",
                CrossoverStatus.None);
        }

        // Default: HOLD
        return (AlgoAction.Hold,
            $"MACD stable: MACD={m.MacdLine:F4}, Signal={m.SignalLine:F4}, Hist={m.Histogram:F4}",
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
}