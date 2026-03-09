using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Pure CCI indicator strategy (stateless from a trade-decision perspective).
/// Produces CCI derivatives for the central decision engine.
/// </summary>
public sealed class CciAlgoStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ICciSettingsService _settingsService;
    private readonly CciEngine _cciEngine;
    private readonly ILogger<CciAlgoStrategy> _logger;

    public string Name => "CCI Indicator";
    public string Description => "Pure CCI indicator with derivative data (current/prev/acceleration).";

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
            var cciPeriod = CciSettings.NormalizePeriod(settings.Period);
            var threshold = CciSettings.NormalizeEntryThreshold(settings.EntryThreshold);

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

            var (currentCci, previousCci, previousCci2) = _cciEngine.GetCciWithHistory(symbol.Symbol, interval);

            if (!currentCci.HasValue)
            {
                return Neutral(symbol, "CCI unavailable");
            }

            double? d1 = null;
            double? d2 = null;
            double? acceleration = null;

            if (previousCci.HasValue)
            {
                d1 = currentCci.Value - previousCci.Value;
            }

            if (previousCci.HasValue && previousCci2.HasValue)
            {
                d2 = previousCci.Value - previousCci2.Value;
                acceleration = d1 - d2;
            }

            bool isAbove = currentCci.Value > threshold;

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"CCI {currentCci.Value:F2} ({(isAbove ? ">" : "<=")} {threshold:F0}) | prev {Format(previousCci)} | prev2 {Format(previousCci2)} | d1 {Format(d1)} | d2 {Format(d2)} | acc {Format(acceleration)}",
                Timestamp: DateTime.UtcNow,
                CciValue: currentCci.Value,
                CciSignal: isAbove ? "ABOVE_ENTRY_THRESHOLD" : "BELOW_ENTRY_THRESHOLD",
                PreviousCciValue: previousCci,
                PreviousCciValue2: previousCci2,
                CciDelta: d1,
                CciDeltaPrevious: d2,
                CciAcceleration: acceleration
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CCI indicator failed for {Symbol}", symbol.Symbol);
            return Neutral(symbol, $"CCI error: {ex.Message}");
        }
    }

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

    private static string Format(double? value) => value.HasValue ? value.Value.ToString("F2") : "n/a";
}
