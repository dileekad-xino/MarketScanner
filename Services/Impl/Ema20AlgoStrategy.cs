using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Pure EMA-20 indicator (stateless).
///
/// Responsibilities:
/// - Calculate EMA-20
/// - Report price position relative to EMA-20
///
/// Non-responsibilities:
/// - NO Buy / Sell decisions
/// - NO position awareness
/// - NO execution logic
///
/// Trade execution is handled exclusively by AlgoStrategy.
/// </summary>
public sealed class Ema20AlgoStrategy : IAlgoStrategy
{
    private const int Period = 20;

    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ILogger<Ema20AlgoStrategy> _logger;
    private readonly EmaEngine _emaEngine;

    public string Name => "EMA-20 Indicator";
    public string Description => "Pure EMA-20 trend indicator (above / below EMA-20).";

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

            // =========================
            // Initialize engine once
            // =========================

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

            // =========================
            // Get EMA-20 value
            // =========================

            var ema20 = _emaEngine.GetEma(symbol.Symbol, interval, Period);
            if (!ema20.HasValue)
            {
                return Neutral(symbol, "EMA-20 unavailable");
            }

            var price = (double)symbol.LastPrice;
            var emaValue = ema20.Value;

            string emaSignal =
                price > emaValue
                    ? "ABOVE_EMA20"
                    : "BELOW_EMA20";

            // =========================
            // PURE indicator result
            // =========================

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold, // <-- always HOLD
                Price: symbol.LastPrice,
                Reason: $"Price {price:F2} {(price > emaValue ? ">" : "<=")} EMA-20 {emaValue:F2}",
                Timestamp: DateTime.UtcNow,
                Ema20Value: emaValue,
                Ema20Signal: emaSignal
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EMA-20 indicator failed for {Symbol}", symbol.Symbol);
            return Neutral(symbol, $"EMA-20 error: {ex.Message}");
        }
    }

    // =========================
    // Helpers
    // =========================

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
        s switch
        {
            15 => "15s",
            30 => "30s",
            60 => "1min",
            _ => $"{s}s"
        };
}
