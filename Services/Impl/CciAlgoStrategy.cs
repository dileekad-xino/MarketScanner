using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace MarketScanner.Services.Impl;

/// <summary>
/// CCI strategy with dual-state engine:
/// - CCI preview updates on each bar (for live UI display)
/// - CCI committed state updates on candle close (matches TradingView)
/// - Strategy evaluates using current CCI (preview during intrabar, committed after close)
/// - Historical candles are used only for initial warm-up (engine init)
/// </summary>
public class CciAlgoStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ICciSettingsService _settingsService;
    private readonly CciEngine _cciEngine;
    private readonly ILogger<CciAlgoStrategy> _logger;

    public string Name => "CCI Strategy";
    public string Description => "Professional intraday CCI: overbought (>=100), oversold (<=-100), bullish (>0), bearish (<0). Auto-adjusts period by timeframe.";

    public CciAlgoStrategy(
        ICandlestickStorage candlestickStorage,
        CandlestickConfig config,
        ICciSettingsService settingsService,
        CciEngine cciEngine,
        ILogger<CciAlgoStrategy> logger,
        ITradeService? tradeService = null)
    {
        _candlestickStorage = candlestickStorage;
        _config = config;
        _settingsService = settingsService;
        _cciEngine = cciEngine;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            var settings = await _settingsService.GetAsync(ct).ConfigureAwait(false);
            var interval = GetIntervalString(_config.IntervalSeconds);

            // Calculate CCI period based on timeframe (professional intraday scalping)
            var cciPeriod = CalculateCciPeriod(_config.IntervalSeconds);

            // Initialize once if needed (historical warm-up)
            if (!_cciEngine.TryGetState(symbol.Symbol, interval, out _))
            {
                var candles = _candlestickStorage
                    .GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                _cciEngine.Initialize(symbol.Symbol, interval, candles, cciPeriod);
            }

            var cci = _cciEngine.GetCci(symbol.Symbol, interval);
            if (!cci.HasValue)
            {
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: "CCI unavailable (engine not initialized yet)",
                    Timestamp: DateTime.UtcNow,
                    CciValue: null,
                    CciSignal: "NEUTRAL"
                );
            }

            AlgoAction action;
            string signal;
            string reason;

            // Professional intraday CCI decision tree
            if (cci.Value >= settings.Overbought)
            {
                action = AlgoAction.Sell;
                signal = "SELL";
                reason = $"CCI overbought >= {settings.Overbought}: {cci.Value:F2}";
            }
            else if (cci.Value <= settings.Oversold)
            {
                action = AlgoAction.Buy;
                signal = "BUY";
                reason = $"CCI oversold <= {settings.Oversold}: {cci.Value:F2}";
            }
            else if (cci.Value > 0)
            {
                action = AlgoAction.Buy;
                signal = "BUY";
                reason = $"CCI bullish momentum above 0: {cci.Value:F2}";
            }
            else if (cci.Value < 0)
            {
                action = AlgoAction.Sell;
                signal = "SELL";
                reason = $"CCI bearish momentum below 0: {cci.Value:F2}";
            }
            else
            {
                action = AlgoAction.Hold;
                signal = "NEUTRAL";
                reason = $"CCI neutral at zero: {cci.Value:F2}";
            }

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: action,
                Price: symbol.LastPrice,
                Reason: reason,
                Timestamp: DateTime.UtcNow,
                CciValue: cci.Value,
                CciSignal: signal
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CCI failed for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"CCI error: {ex.Message}",
                Timestamp: DateTime.UtcNow,
                CciValue: null,
                CciSignal: "NEUTRAL"
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
    /// Calculates CCI period based on candlestick interval for professional intraday scalping.
    /// Lower timeframe = shorter CCI period for faster response.
    /// </summary>
    private int CalculateCciPeriod(int intervalSeconds)
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

