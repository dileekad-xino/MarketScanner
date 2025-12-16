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

            var candles = _candlestickStorage
                .GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                .OrderBy(c => c.Timestamp)
                .ToList();

            var min = _config.Macd.SlowPeriod + _config.Macd.SignalPeriod;

            if (candles.Count < min)
            {
                return new AlgoResult(
                    symbol.Symbol,
                    AlgoAction.Hold,
                    symbol.LastPrice,
                    $"Need {min} candles, got {candles.Count}",
                    DateTime.UtcNow
                );
            }

            if (!_macdEngine.TryGetState(symbol.Symbol, interval, out _))
            {
                _logger.LogInformation("Initializing MACD engine for {Symbol}", symbol.Symbol);
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
                return Hold(symbol, "MACD unavailable");

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
        const decimal EPS = 0.0000001m;

        bool above = (m.MacdLine - m.SignalLine) > EPS;
        bool below = (m.SignalLine - m.MacdLine) > EPS;

        var prev = _macdEngine.GetPreviousMacd(m.Symbol, m.Interval);

        bool bullish = false;
        bool bearish = false;

        if (prev != null)
        {
            decimal prevMacd = (decimal)prev.Value.Macd;
            decimal prevSignal = (decimal)prev.Value.Signal;

            bullish = prevMacd <= prevSignal + EPS && above;
            bearish = prevMacd >= prevSignal - EPS && below;
        }


        if (bullish && m.MacdLine > 0 && m.SignalLine > 0)
            return (AlgoAction.Buy,
                $"Bullish crossover: MACD={m.MacdLine:F4}, Signal={m.SignalLine:F4}",
                CrossoverStatus.CrossedUp);

        if (bearish)
            return (AlgoAction.Sell,
                $"Bearish crossover: MACD={m.MacdLine:F4}, Signal={m.SignalLine:F4}",
                CrossoverStatus.CrossedDown);

        return (AlgoAction.Hold,
            $"Monitoring: MACD={m.MacdLine:F4}, Signal={m.SignalLine:F4}, Hist={m.Histogram:F4}",
            CrossoverStatus.None);
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
