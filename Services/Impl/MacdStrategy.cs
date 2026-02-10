using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Pure MACD momentum indicator (stateless).
///
/// Responsibilities:
/// - Calculate MACD
/// - Report bullish momentum state:
///     DARK_GREEN  -> histogram > 0 and expanding
///     LIGHT_GREEN -> histogram > 0 but contracting
///     NEUTRAL     -> histogram <= 0
///
/// Non-responsibilities:
/// - NO Buy / Sell
/// - NO position awareness
/// - NO execution logic
///
/// Trade decisions are handled exclusively by AlgoStrategy.
/// </summary>
public sealed class MacdStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ILogger<MacdStrategy> _logger;
    private readonly MacdEngine _macdEngine;

    public string Name => "MACD Indicator";
    public string Description => "Pure MACD momentum indicator (dark green / light green).";

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

            if (!_macdEngine.TryGetState(symbol.Symbol, interval, out _))
            {
                var candles = _candlestickStorage
                    .GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                if (candles.Count == 0)
                {
                    return Neutral(symbol, "No candles available for MACD initialization");
                }

                _macdEngine.Initialize(
                    symbol.Symbol,
                    interval,
                    candles,
                    _config.Macd.FastPeriod,
                    _config.Macd.SlowPeriod,
                    _config.Macd.SignalPeriod);
            }

            // =========================
            // Get MACD snapshot
            // =========================

            var last = _macdEngine.GetLastMacd(symbol.Symbol, interval);
            if (last == null)
                return Neutral(symbol, "MACD unavailable");

            var (macd, signal, histRaw) = last.Value;
            decimal hist = (decimal)histRaw;

            var prev = _macdEngine.GetPreviousMacd(symbol.Symbol, interval);
            decimal? prevHist = null;

            if (prev != null)
                prevHist = (decimal)prev.Value.Macd - (decimal)prev.Value.Signal;

            // =========================
            // Momentum classification
            // =========================

            string macdSignal;

            if (hist > 0m && prevHist.HasValue && hist > prevHist.Value)
            {
                macdSignal = "DARK_GREEN";   // bullish + expanding
            }
            else if (hist > 0m && prevHist.HasValue && hist <= prevHist.Value)
            {
                macdSignal = "LIGHT_GREEN";  // bullish but weakening
            }
            else
            {
                macdSignal = "NEUTRAL";      // not bullish
            }

            var macdData = new MacdData(
                symbol.Symbol,
                (decimal)macd,
                (decimal)signal,
                hist,
                DateTime.UtcNow,
                interval
            );

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold, // <-- always HOLD
                Price: symbol.LastPrice,
                Reason: $"MACD {macdSignal}: Hist={hist:F4}, PrevHist={prevHist?.ToString("F4") ?? "N/A"}",
                Timestamp: DateTime.UtcNow,
                Macd: macdData,
                Crossover: CrossoverStatus.None,
                MacdSignal: macdSignal
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MACD indicator failed for {Symbol}", symbol.Symbol);
            return Neutral(symbol, $"MACD error: {ex.Message}");
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
            MacdSignal: "NEUTRAL"
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
