using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

/// <summary>
/// EMA 20 strategy that generates buy signals when price is above EMA 20.
/// Uses the generic EmaEngine for real-time monitoring.
/// </summary>
public class Ema20AlgoStrategy : IAlgoStrategy
{
    private const int Period = 20;
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ILogger<Ema20AlgoStrategy> _logger;
    private readonly EmaEngine _emaEngine;

    public string Name => "EMA 20 Strategy";
    public string Description => "Generates buy signal when price is above EMA 20.";

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

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            var interval = GetIntervalString(_config.IntervalSeconds);

            // Initialize once if needed (historical warm-up)
            if (!_emaEngine.TryGetState(symbol.Symbol, interval, Period, out _))
            {
                var candles = _candlestickStorage
                    .GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                _logger.LogInformation("Initializing EMA 20 engine for {Symbol} (warm-up from {Count} candles)", symbol.Symbol, candles.Count);
                _emaEngine.Initialize(symbol.Symbol, interval, candles, Period);
            }

            var ema20 = _emaEngine.GetEma(symbol.Symbol, interval, Period);
            if (!ema20.HasValue)
            {
                return Hold(symbol, "EMA 20 unavailable (engine not initialized yet)");
            }

            var currentPrice = (double)symbol.LastPrice;
            var ema20Value = ema20.Value;

            // Signal logic: BUY if price > EMA 20, otherwise HOLD
            AlgoAction action;
            string signal;
            string reason;

            if (currentPrice > ema20Value)
            {
                action = AlgoAction.Buy;
                signal = "Buy";
                reason = $"Price {currentPrice:F2} > EMA 20 {ema20Value:F2}";
            }
            else
            {
                action = AlgoAction.Hold;
                signal = "Hold";
                reason = $"Price {currentPrice:F2} <= EMA 20 {ema20Value:F2}";
            }

            return new AlgoResult(
                symbol.Symbol,
                action,
                symbol.LastPrice,
                reason,
                DateTime.UtcNow,
                Ema20Value: ema20Value,
                Ema20Signal: signal
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EMA 20 failed for {Symbol}", symbol.Symbol);
            return Hold(symbol, $"EMA 20 error: {ex.Message}");
        }
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
