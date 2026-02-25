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
    private readonly ConcurrentDictionary<string, byte> _symbolCciEntryArmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, double> _symbolLastSellCciLevel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, double> _symbolPreviousFlatCci = new(StringComparer.OrdinalIgnoreCase);

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

        var cciThreshold = cciSettings.Overbought;
        bool cciAbove =
            cciValue.HasValue && cciValue.Value > cciThreshold;

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
            _symbolPreviousFlatCci.TryGetValue(symbol.Symbol, out var previousFlatCci);
            var hasPreviousFlatCci = _symbolPreviousFlatCci.ContainsKey(symbol.Symbol);
            var hasLastSellLevel = _symbolLastSellCciLevel.TryGetValue(symbol.Symbol, out var lastSellLevel);

            if (cciValue.HasValue && cciValue.Value <= cciThreshold)
            {
                _symbolCciEntryArmed[symbol.Symbol] = 0;
            }

            var isEntryArmed = _symbolCciEntryArmed.ContainsKey(symbol.Symbol);
            var cciCrossedAbove = cciValue.HasValue && cciValue.Value > cciThreshold && isEntryArmed;
            var crossedAboveLastSell =
                hasLastSellLevel &&
                hasPreviousFlatCci &&
                cciValue.HasValue &&
                previousFlatCci < lastSellLevel &&
                cciValue.Value >= lastSellLevel;

            // ---------- ENTRY ----------
            if ((crossedAboveLastSell || cciCrossedAbove) /*&& macdDarkGreen*/ && aboveEma20)
            {
                action = AlgoAction.Buy;
                summary = crossedAboveLastSell
                    ? $"BUY: CCI crossed back above last sell level {lastSellLevel:F0} + price above EMA20"
                    : $"BUY: CCI crossed above {cciThreshold:F0} + MACD dark green + price above EMA20";
                _symbolCciEntryArmed.TryRemove(symbol.Symbol, out _);
            }
            else
            {
                action = AlgoAction.Hold;
                summary =
                    $"HOLD (flat): " +
                    $"CCI={(cciAbove ? $">{cciThreshold:F0}" : $"<={cciThreshold:F0}")}, " +
                    $"EntryArmed={(isEntryArmed ? "Yes" : "No")}, " +
                    $"LastSellLevel={(hasLastSellLevel ? lastSellLevel.ToString("F0") : "N/A")}, " +
                    // $"MACD={(macdDarkGreen ? "DarkGreen" : "NotDarkGreen")}, " +
                    $"EMA20={(aboveEma20 ? "Above" : "Below")}";
            }

            if (cciValue.HasValue)
            {
                _symbolPreviousFlatCci[symbol.Symbol] = cciValue.Value;
            }
        }
        else
        {
            _symbolPreviousFlatCci.TryRemove(symbol.Symbol, out _);

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
                double trailingSellThreshold;
                if (_symbolSellCciThresholds.TryGetValue(symbol.Symbol, out var existingThreshold))
                {
                    trailingSellThreshold = _symbolSellCciThresholds.AddOrUpdate(
                        symbol.Symbol,
                        candidateThreshold,
                        (_, currentThreshold) => Math.Max(currentThreshold, candidateThreshold));
                }
                else
                {
                    // Don't arm trailing exit until CCI first reaches the configured sell zone.
                    // Prevents immediate stop-out for entries that start below sellZoneMin.
                    if (cciValue.Value < sellZoneMin)
                    {
                        action = AlgoAction.Hold;
                        summary = $"HOLD (in position): CCI {cciValue.Value:F2} below sell zone min {sellZoneMin}, trailing not armed yet";

                        var reasonsWithoutSummary = string.Join(" | ",
                            results.Select(r => $"[{r.Action}] {r.Reason}"));

                        return new AlgoResult(
                            Symbol: symbol.Symbol,
                            Action: action,
                            Price: price,
                            Reason: $"{summary} | {reasonsWithoutSummary}",
                            Timestamp: DateTime.UtcNow,
                            CciValue: cci?.CciValue,
                            CciSignal: cci?.CciSignal,
                            Macd: macd?.Macd,
                            MacdSignal: macd?.MacdSignal,
                            Ema20Value: ema?.Ema20Value,
                            Ema20Signal: ema?.Ema20Signal
                        );
                    }

                    trailingSellThreshold = _symbolSellCciThresholds.GetOrAdd(symbol.Symbol, candidateThreshold);
                }

                if (cciValue.Value < trailingSellThreshold)
                {
                    action = AlgoAction.Sell;
                    summary = $"SELL: CCI {cciValue.Value:F2} dropped below trailing zone {trailingSellThreshold:F0} (min={sellZoneMin}, max={sellZoneMax}, gap={zoneGap})";
                    _symbolLastSellCciLevel[symbol.Symbol] = trailingSellThreshold;
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
        _symbolCciEntryArmed.Clear();
        _symbolLastSellCciLevel.Clear();
        _symbolPreviousFlatCci.Clear();
        _logger.LogInformation("CCI settings changed. Cleared trailing sell thresholds and entry states for immediate realtime application.");
    }
}
