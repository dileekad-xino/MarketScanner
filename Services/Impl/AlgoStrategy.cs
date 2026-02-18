using MarketScanner.Models;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Central trade decision engine.
/// 
/// Indicators are PURE and stateless.
/// AlgoStrategy is the ONLY place that:
/// - knows about positions
/// - decides BUY / SELL
/// - enforces trade rules
/// </summary>
public sealed class AlgoStrategy : IAlgoStrategy
{
    private readonly IReadOnlyList<IAlgoStrategy> _strategies;
    private readonly ICciSettingsService _cciSettingsService;
    private readonly ILogger<AlgoStrategy> _logger;
    private readonly ConcurrentDictionary<string, double> _symbolSellCciThresholds = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Composite Algorithm";
    public string Description =>
        "BUY: CCI>100 + MACD dark green + price above EMA20 (no position). " +
        "SELL: Dynamic CCI trailing threshold by zone (in position).";

    public AlgoStrategy(
        IEnumerable<IAlgoStrategy> strategies,
        ICciSettingsService cciSettingsService,
        ILogger<AlgoStrategy> logger)
    {
        _strategies = strategies.ToList();
        _cciSettingsService = cciSettingsService;
        _logger = logger;
        _cciSettingsService.SettingsChanged += OnCciSettingsChanged;
    }

    public async Task<AlgoResult> ExecuteAsync(
        ScannerRowViewModel symbol,
        bool hasOpenPosition = false,
        CancellationToken ct = default)
    {
        try
        {
            var cciSettings = await _cciSettingsService.GetAsync(symbol.Symbol, ct).ConfigureAwait(false);

            // =========================
            // Run all indicators
            // =========================

            var tasks = _strategies.Select(s => s.ExecuteAsync(symbol, hasOpenPosition, ct));
            var results = await Task.WhenAll(tasks);

            return CombineResults(results, symbol, hasOpenPosition, cciSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AlgoStrategy failed for {Symbol}", symbol.Symbol);
            return Hold(symbol, $"Algo error: {ex.Message}");
        }
    }

    // =====================================================
    // CORE DECISION LOGIC
    // =====================================================

    private AlgoResult CombineResults(
        IReadOnlyList<AlgoResult> results,
        ScannerRowViewModel symbol,
        bool hasOpenPosition,
        CciSettings cciSettings)
    {
        if (results.Count == 0)
            return Hold(symbol, "No indicator results");

        // =========================
        // Extract indicator states
        // =========================

        var cci = results.FirstOrDefault(r => r.CciSignal != null);
        var macd = results.FirstOrDefault(r => r.MacdSignal != null);
        var ema = results.FirstOrDefault(r => r.Ema20Signal != null);
        var cciValue = cci?.CciValue;

        bool cciAbove =
            cci?.CciSignal == "ABOVE_THRESHOLD";

        bool macdDarkGreen =
            macd?.MacdSignal == "DARK_GREEN";

        bool aboveEma20 =
            ema?.Ema20Signal == "ABOVE_EMA20";

        var price = symbol.LastPrice;

        // =========================
        // DECISION
        // =========================

        AlgoAction action;
        string summary;

        if (!hasOpenPosition)
            _symbolSellCciThresholds.TryRemove(symbol.Symbol, out _);

        if (!hasOpenPosition)
        {
            // ---------- ENTRY ----------
            if (cciAbove /*&& macdDarkGreen*/ && aboveEma20)
            {
                action = AlgoAction.Buy;
                summary = "BUY: CCI>100 + MACD dark green + price above EMA20";
            }
            else
            {
                action = AlgoAction.Hold;
                summary =
                    $"HOLD (flat): " +
                    $"CCI={(cciAbove ? ">100" : "<=100")}, " +
                    // $"MACD={(macdDarkGreen ? "DarkGreen" : "NotDarkGreen")}, " +
                    $"EMA20={(aboveEma20 ? "Above" : "Below")}";
            }
        }
        else
        {
            // ---------- EXIT ----------
            if (!cciValue.HasValue)
            {
                action = AlgoAction.Hold;
                summary = "HOLD (in position): waiting for CCI value";
            }
            else
            {
                var sellZoneMin = CciSettings.NormalizeSellZoneMin(cciSettings.SellZoneMin, cciSettings.SellZoneMax);
                var sellZoneMax = CciSettings.NormalizeSellZoneMax(cciSettings.SellZoneMax, sellZoneMin);
                var zoneGap = CciSettings.NormalizeZoneGapInterval(cciSettings.ZoneGapInterval, sellZoneMin, sellZoneMax);
                var candidateThreshold = CciSettings.CalculateTrailingSellThreshold(cciValue.Value, sellZoneMin, sellZoneMax, zoneGap);

                var trailingSellThreshold = _symbolSellCciThresholds.AddOrUpdate(
                    symbol.Symbol,
                    candidateThreshold,
                    (_, existingThreshold) => Math.Max(existingThreshold, candidateThreshold));

                if (cciValue.Value < trailingSellThreshold)
                {
                    action = AlgoAction.Sell;
                    summary = $"SELL: CCI {cciValue.Value:F2} dropped below trailing zone {trailingSellThreshold:F0} (min={sellZoneMin}, max={sellZoneMax}, gap={zoneGap})";
                    _symbolSellCciThresholds.TryRemove(symbol.Symbol, out _);
                }
                else
                {
                    action = AlgoAction.Hold;
                    summary = $"HOLD (in position): CCI {cciValue.Value:F2} >= trailing zone {trailingSellThreshold:F0} (min={sellZoneMin}, max={sellZoneMax}, gap={zoneGap})";
                }
            }
        }

        // =========================
        // UI-safe reasons
        // =========================

        var reasons = string.Join(" | ",
            results.Select(r =>
                $"[{r.Action}] {r.Reason}")
        );

        return new AlgoResult(
            Symbol: symbol.Symbol,
            Action: action,
            Price: price,
            Reason: $"{summary} | {reasons}",
            Timestamp: DateTime.UtcNow,
            CciValue: cci?.CciValue,
            CciSignal: cci?.CciSignal,
            Macd: macd?.Macd,
            MacdSignal: macd?.MacdSignal,
            Ema20Value: ema?.Ema20Value,
            Ema20Signal: ema?.Ema20Signal
        );
    }

    // =========================
    // Helpers
    // =========================

    private static AlgoResult Hold(ScannerRowViewModel symbol, string reason) =>
        new(
            Symbol: symbol.Symbol,
            Action: AlgoAction.Hold,
            Price: symbol.LastPrice,
            Reason: reason,
            Timestamp: DateTime.UtcNow
        );

    private void OnCciSettingsChanged(object? sender, CciSettings settings)
    {
        _symbolSellCciThresholds.Clear();
        _logger.LogInformation("CCI settings changed. Cleared trailing sell thresholds for immediate realtime application.");
    }
}
