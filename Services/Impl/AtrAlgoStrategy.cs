using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Pure ATR volatility indicator strategy (stateless and indicator-only).
/// </summary>
public sealed class AtrAlgoStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ICciSettingsService _settingsService;
    private readonly AtrEngine _atrEngine;
    private readonly ILogger<AtrAlgoStrategy> _logger;

    public string Name => "ATR Indicator";
    public string Description => "Pure ATR volatility indicator.";

    public AtrAlgoStrategy(
        ICandlestickStorage candlestickStorage,
        CandlestickConfig config,
        ICciSettingsService settingsService,
        AtrEngine atrEngine,
        ILogger<AtrAlgoStrategy> logger)
    {
        _candlestickStorage = candlestickStorage;
        _config = config;
        _settingsService = settingsService;
        _atrEngine = atrEngine;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, bool hasOpenPosition = false, CancellationToken ct = default)
    {
        try
        {
            var settings = await _settingsService.GetAsync(symbol.Symbol, ct).ConfigureAwait(false);
            var interval = TimeframeMap.ToIntervalKey(_config.IntervalSeconds);
            var period = CciSettings.NormalizeAtrPeriod(settings.AtrPeriod);

            var candles = _candlestickStorage
                .GetCandlesticks(symbol.Symbol, interval, period + 200)
                .OrderBy(c => c.Timestamp)
                .ToList();

            var atr = _atrEngine.CalculateAtr(candles, period);
            if (!atr.HasValue)
            {
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"ATR unavailable (need >= {period + 1} candles, got {candles.Count})",
                    Timestamp: DateTime.UtcNow,
                    AtrValue: null);
            }

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"ATR({period}) {atr.Value:F4}",
                Timestamp: DateTime.UtcNow,
                AtrValue: atr.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ATR indicator failed for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"ATR error: {ex.Message}",
                Timestamp: DateTime.UtcNow,
                AtrValue: null);
        }
    }
}
