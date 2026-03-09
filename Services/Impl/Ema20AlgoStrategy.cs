using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Pure EMA-20 indicator (stateless).
/// </summary>
public sealed class Ema20AlgoStrategy : IAlgoStrategy
{
    private const int Period = 20;

    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ILogger<Ema20AlgoStrategy> _logger;
    private readonly EmaEngine _emaEngine;

    public string Name => "EMA-20 Indicator";
    public string Description => "Pure EMA-20 trend indicator with current/previous values.";

    public Ema20AlgoStrategy(
        ICandlestickStorage storage,
        CandlestickConfig config,
        ILogger<Ema20AlgoStrategy> logger,
        EmaEngine engine)
    {
        _candlestickStorage = storage;
        _config = config;
        _logger = logger;
        _emaEngine = engine;
    }

    public async Task<AlgoResult> ExecuteAsync(
        ScannerRowViewModel symbol,
        bool hasOpenPosition = false,
        CancellationToken ct = default)
    {
        try
        {
            var interval = GetIntervalString(_config.IntervalSeconds);

            if (!_emaEngine.TryGetState(symbol.Symbol, interval, Period, out _))
            {
                var candles = _candlestickStorage
                    .GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                if (candles.Count == 0)
                {
                    return Neutral(symbol, "No candles available for EMA-20 initialization");
                }

                _emaEngine.Initialize(symbol.Symbol, interval, candles, Period);
            }

            var (ema20, previousEma20) = _emaEngine.GetEmaWithPrevious(symbol.Symbol, interval, Period);
            if (!ema20.HasValue)
            {
                return Neutral(symbol, "EMA-20 unavailable");
            }

            var price = symbol.LastPrice;
            var emaValue = ema20.Value;
            var emaSignal = price > emaValue ? "ABOVE_EMA20" : "BELOW_EMA20";

            var recentCandles = _candlestickStorage
                .GetCandlesticks(symbol.Symbol, interval, 2)
                .OrderBy(c => c.Timestamp)
                .ToList();

            double? previousClose = recentCandles.Count >= 2
                ? (double)recentCandles[^2].Close
                : null;

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: price,
                Reason: $"Price {price:F2} {(price > emaValue ? ">" : "<=")} EMA-20 {emaValue:F2} | prev EMA {Format(previousEma20)} | prev close {Format(previousClose)}",
                Timestamp: DateTime.UtcNow,
                Ema20Value: emaValue,
                PreviousEma20Value: previousEma20,
                PreviousClose: previousClose,
                Ema20Signal: emaSignal
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EMA-20 indicator failed for {Symbol}", symbol.Symbol);
            return Neutral(symbol, $"EMA-20 error: {ex.Message}");
        }
    }

    private static AlgoResult Neutral(ScannerRowViewModel symbol, string reason) =>
        new(
            Symbol: symbol.Symbol,
            Action: AlgoAction.Hold,
            Price: symbol.LastPrice,
            Reason: reason,
            Timestamp: DateTime.UtcNow,
            Ema20Signal: "NEUTRAL"
        );

    private static string GetIntervalString(int s) =>
        TimeframeMap.ToIntervalKey(s);

    private static string Format(double? value) => value.HasValue ? value.Value.ToString("F2") : "n/a";
}
