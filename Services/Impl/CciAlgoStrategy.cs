using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace MarketScanner.Services.Impl;

/// <summary>
/// CCI momentum crossover strategy with dual-state engine:
/// - CCI preview updates on each bar (for live UI display)
/// - CCI committed state updates on candle close (matches TradingView)
/// - Strategy generates BUY signal when CCI crosses above +100
/// - Strategy holds position while CCI remains above +100
/// - Strategy generates SELL signal when CCI crosses below +100
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
    public string Description => "Momentum crossover: BUY on cross above +100, hold while above +100, SELL on cross below +100. Uses 1-minute bars.";

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

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, bool hasOpenPosition = false, CancellationToken ct = default)
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

            // Get current and previous CCI values for crossover detection
            var (currentCci, previousCci) = _cciEngine.GetCciWithPrevious(symbol.Symbol, interval);
            if (!currentCci.HasValue)
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

            // Get threshold from settings (default +100)
            var threshold = settings.Overbought;

            // Crossover detection
            bool crossedAbove = previousCci.HasValue && previousCci.Value < threshold && currentCci.Value >= threshold;
            bool crossedBelow = previousCci.HasValue && previousCci.Value >= threshold && currentCci.Value < threshold;
            bool isAbove = currentCci.Value >= threshold;

            AlgoAction action;
            string signal;
            string reason;

            // Momentum crossover strategy logic
            if (crossedAbove && currentCci.Value > 100)
            {
                action = AlgoAction.Buy;
                signal = "BUY_CROSS_ABOVE";
                reason = $"CCI crossed above +{threshold:F0}: {previousCci.Value:F2} → {currentCci.Value:F2}";

                // Update EntryCciValue in state
                if (_cciEngine.TryGetState(symbol.Symbol, interval, out var state))
                {
                    state.EntryCciValue = currentCci.Value;
                }
            }
            else if (crossedBelow)
            {
                action = AlgoAction.Sell;
                signal = "SELL_CROSS_BELOW";
                reason = $"CCI crossed below +{threshold:F0}: {previousCci.Value:F2} → {currentCci.Value:F2}";

                // Update ExitCciValue in state
                if (_cciEngine.TryGetState(symbol.Symbol, interval, out var state))
                {
                    state.ExitCciValue = currentCci.Value;
                }
            }
            else if (isAbove)
            {
                action = AlgoAction.Hold;
                signal = "HOLD_ABOVE_100";
                reason = $"CCI above +{threshold:F0}, holding position: {currentCci.Value:F2}";
            }
            else
            {
                action = AlgoAction.Hold;
                signal = "HOLD_BELOW_100";
                reason = $"CCI below +{threshold:F0}, no position: {currentCci.Value:F2}";
            }

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: action,
                Price: symbol.LastPrice,
                Reason: reason,
                Timestamp: DateTime.UtcNow,
                CciValue: currentCci.Value,
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

