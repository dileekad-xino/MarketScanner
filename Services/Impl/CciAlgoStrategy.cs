using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Pure CCI momentum indicator (stateless).
/// 
/// Responsibilities:
/// - Calculate CCI
/// - Indicate whether momentum is ABOVE or BELOW threshold
/// 
/// Non-responsibilities:
/// - NO position tracking
/// - NO BUY / SELL decisions
/// - NO holding logic
/// 
/// Trade execution is handled exclusively by AlgoStrategy.
/// </summary>
public sealed class CciAlgoStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ICciSettingsService _settingsService;
    private readonly CciEngine _cciEngine;
    private readonly ILogger<CciAlgoStrategy> _logger;

    public string Name => "CCI Indicator";
    public string Description => "Pure CCI momentum indicator. Reports ABOVE / BELOW threshold only.";

    public CciAlgoStrategy(
        ICandlestickStorage candlestickStorage,
        CandlestickConfig config,
        ICciSettingsService settingsService,
        CciEngine cciEngine,
        ILogger<CciAlgoStrategy> logger)
    {
        _candlestickStorage = candlestickStorage;
        _config = config;
        _settingsService = settingsService;
        _cciEngine = cciEngine;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(
        ScannerRowViewModel symbol,
        bool hasOpenPosition = false,
        CancellationToken ct = default)
    {
        try
        {
            var settings = await _settingsService.GetAsync(symbol.Symbol, ct).ConfigureAwait(false);
            var interval = GetIntervalString(_config.IntervalSeconds);
            var cciPeriod = settings.Period > 1 ? settings.Period : 20;
            var threshold = settings.Overbought;

            // =========================
            // Initialize engine once
            // =========================

            if (!_cciEngine.TryGetState(symbol.Symbol, interval, out _))
            {
                var candles = _candlestickStorage
                    .GetCandlesticks(symbol.Symbol, interval, int.MaxValue)
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                if (candles.Count < cciPeriod)
                {
                    return Neutral(symbol, $"Insufficient candles for CCI initialization (need {cciPeriod}, got {candles.Count})");
                }

                _cciEngine.Initialize(symbol.Symbol, interval, candles, cciPeriod);
            }

            // =========================
            // Get CCI value
            // =========================

            var (currentCci, _) = _cciEngine.GetCciWithPrevious(symbol.Symbol, interval);

            if (!currentCci.HasValue)
            {
                return Neutral(symbol, "CCI unavailable");
            }

            bool isAbove = currentCci.Value >= threshold;

            // =========================
            // PURE indicator result
            // =========================

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold, // <-- always HOLD
                Price: symbol.LastPrice,
                Reason: isAbove
                    ? $"CCI ABOVE +{threshold:F0}: {currentCci.Value:F2}"
                    : $"CCI BELOW +{threshold:F0}: {currentCci.Value:F2}",
                Timestamp: DateTime.UtcNow,
                CciValue: currentCci.Value,
                CciSignal: isAbove ? "ABOVE_THRESHOLD" : "BELOW_THRESHOLD"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CCI indicator failed for {Symbol}", symbol.Symbol);
            return Neutral(symbol, $"CCI error: {ex.Message}");
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
            CciValue: null,
            CciSignal: "NEUTRAL"
        );

    private static string GetIntervalString(int intervalSeconds) =>
        TimeframeMap.ToIntervalKey(intervalSeconds);

}
