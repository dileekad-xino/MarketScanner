using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace MarketScanner.Services.Impl;

/// <summary>
/// RSI strategy with dual-state engine:
/// - RSI preview updates on each tick (for live UI display)
/// - RSI committed state updates on candle close (matches TradingView)
/// - Strategy evaluates using current RSI (preview during intrabar, committed after close)
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
    public string Description => "Professional intraday RSI scalping: momentum (50-75), pullback (40-50), exhaustion (>=75), exit (<40). Auto-adjusts period by timeframe.";

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

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, bool hasOpenPosition = false, CancellationToken ct = default)
    {
        try
        {
            var settings = await _settingsService.GetAsync(ct).ConfigureAwait(false);
            var interval = GetIntervalString(_config.IntervalSeconds);

            // Calculate RSI period based on timeframe (professional intraday scalping)
            var rsiPeriod = CalculateRsiPeriod(_config.IntervalSeconds);

            // Initialize once if needed (historical warm-up)
            if (!_rsiEngine.TryGetState(symbol.Symbol, interval, out _))
            {
                var candles = _candlestickStorage
                    .GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                _rsiEngine.Initialize(symbol.Symbol, interval, candles, rsiPeriod);
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

            // Professional intraday RSI decision tree
            if (rsi.Value >= 50 && rsi.Value < 75)
            {
                action = AlgoAction.Buy;
                signal = "BUY";
                reason = $"RSI momentum above 50 (bullish regime): {rsi.Value:F2}";
            }
            else if (rsi.Value >= 40 && rsi.Value < 50)
            {
                action = AlgoAction.Buy;
                signal = "BUY";
                reason = $"RSI pullback holding above 40 (trend support): {rsi.Value:F2}";
            }
            else if (rsi.Value >= 75)
            {
                action = AlgoAction.Sell;
                signal = "SELL";
                reason = $"RSI exhaustion zone >= 75: {rsi.Value:F2}";
            }
            else if (rsi.Value < 40)
            {
                action = AlgoAction.Sell;
                signal = "SELL";
                reason = $"RSI lost bullish structure (<40): {rsi.Value:F2}";
            }
            else
            {
                action = AlgoAction.Hold;
                signal = "NEUTRAL";
                reason = $"RSI neutral consolidation: {rsi.Value:F2}";
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

    /// <summary>
    /// Calculates RSI period based on candlestick interval for professional intraday scalping.
    /// Lower timeframe = shorter RSI period for faster response.
    /// </summary>
    private int CalculateRsiPeriod(int intervalSeconds)
    {
        return intervalSeconds switch
        {
            15 => 8,   // 15s: period 7-9, use 8 (middle)
            30 => 10,  // 30s: period 9-12, use 10 (middle)
            60 => 14,  // 1m: period 14 (standard)
            _ => intervalSeconds <= 20 ? 8 : intervalSeconds <= 45 ? 10 : 14  // Fallback logic
        };
    }
}


