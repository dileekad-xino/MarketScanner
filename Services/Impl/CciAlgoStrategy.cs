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
    public string Description => "Professional intraday CCI scalping: momentum (+50 to +200), pullback (0 to +50), exhaustion (>=+200), oversold bounce (<=-200), bearish (<-50). Auto-adjusts period by timeframe.";

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

            // Professional intraday CCI decision tree (scalping-optimized)
            if (cci.Value >= 200)
            {
                action = AlgoAction.Sell;
                signal = "TAKE PROFIT";
                reason = $"CCI exhaustion >= +200: {cci.Value:F2}";
            }
            else if (cci.Value >= 50 && cci.Value < 200)
            {
                action = AlgoAction.Buy;
                signal = "BUY MOMENTUM";
                reason = $"CCI strong bullish momentum > +50: {cci.Value:F2}";
            }
            else if (cci.Value > 0 && cci.Value < 50)
            {
                action = AlgoAction.Buy;
                signal = "BUY PULLBACK";
                reason = $"CCI bullish pullback holding above 0: {cci.Value:F2}";
            }
            else if (cci.Value <= -200)
            {
                action = AlgoAction.Buy;
                signal = "OVERSOLD SCALP";
                reason = $"CCI capitulation <= -200 (fast scalp): {cci.Value:F2}";
            }
            else if (cci.Value <= -50 && cci.Value > -200)
            {
                action = AlgoAction.Sell;
                signal = "SELL MOMENTUM";
                reason = $"CCI bearish momentum < -50: {cci.Value:F2}";
            }
            else if (cci.Value < 0 && cci.Value > -50)
            {
                action = AlgoAction.Sell;
                signal = "EXIT WEAKNESS";
                reason = $"CCI lost bullish structure (<0): {cci.Value:F2}";
            }
            else
            {
                action = AlgoAction.Hold;
                signal = "NEUTRAL";
                reason = $"CCI consolidation near zero: {cci.Value:F2}";
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

