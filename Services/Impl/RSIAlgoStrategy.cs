using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Live-only RSI strategy:
/// - RSI is computed by RsiEngine on each tick (Wilder smoothing)
/// - Strategy evaluates on each tick using the current RSI value only
/// - Historical candles are used only for initial warm-up (engine init)
/// </summary>
public class RSIAlgoStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly IRsiSettingsService _settingsService;
    private readonly RsiEngine _rsiEngine;
    private readonly ILogger<RSIAlgoStrategy> _logger;

    public string Name => "RSI Strategy";
    public string Description => "Live-only RSI strategy using tick-updated RSI (Wilder smoothing).";

    public RSIAlgoStrategy(
        ICandlestickStorage candlestickStorage,
        CandlestickConfig config,
        IRsiSettingsService settingsService,
        RsiEngine rsiEngine,
        ILogger<RSIAlgoStrategy> logger,
        ITradeService? tradeService = null)
    {
        _candlestickStorage = candlestickStorage;
        _config = config;
        _settingsService = settingsService;
        _rsiEngine = rsiEngine;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            var settings = await _settingsService.GetAsync(ct).ConfigureAwait(false);
            var interval = GetIntervalString(_config.IntervalSeconds);

            // Initialize once if needed (historical warm-up)
            if (!_rsiEngine.TryGetState(symbol.Symbol, interval, out _))
            {
                var candles = _candlestickStorage
                    .GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                _rsiEngine.Initialize(symbol.Symbol, interval, candles, settings.Period);
            }

            var rsi = _rsiEngine.GetRsi(symbol.Symbol, interval);
            if (!rsi.HasValue)
            {
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: "RSI unavailable (engine not initialized yet)",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }

            AlgoAction action;
            string signal;
            string reason;

            if (rsi.Value <= settings.Oversold)
            {
                action = AlgoAction.Buy;
                signal = "BUY";
                reason = $"RSI oversold: {rsi.Value:F2} <= {settings.Oversold:F2}";
            }
            else if (rsi.Value >= settings.Overbought)
            {
                action = AlgoAction.Sell;
                signal = "SELL";
                reason = $"RSI overbought: {rsi.Value:F2} >= {settings.Overbought:F2}";
            }
            else
            {
                action = AlgoAction.Hold;
                signal = "NEUTRAL";
                reason = $"RSI {rsi.Value:F2} is neutral between levels";
            }

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: action,
                Price: symbol.LastPrice,
                Reason: reason,
                Timestamp: DateTime.UtcNow,
                RsiValue: rsi.Value,
                RsiSignal: signal
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RSI failed for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"RSI error: {ex.Message}",
                Timestamp: DateTime.UtcNow,
                RsiValue: null,
                RsiSignal: "NEUTRAL"
            );
        }
    }

    private string GetIntervalString(int intervalSeconds) =>
        intervalSeconds switch
        {
            15 => "15s",
            30 => "30s",
            60 => "1min",
            _ => $"{intervalSeconds}s"
        };
}


