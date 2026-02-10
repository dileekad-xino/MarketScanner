using MarketScanner.Models;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<AlgoStrategy> _logger;

    public string Name => "Composite Algorithm";
    public string Description =>
        "BUY: CCI>100 + MACD dark green + price above EMA20 (no position). " +
        "SELL: CCI<100 (in position).";

    public AlgoStrategy(
        IEnumerable<IAlgoStrategy> strategies,
        ILogger<AlgoStrategy> logger)
    {
        _strategies = strategies.ToList();
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(
        ScannerRowViewModel symbol,
        bool hasOpenPosition = false,
        CancellationToken ct = default)
    {
        try
        {
            // =========================
            // Run all indicators
            // =========================

            var tasks = _strategies.Select(s => s.ExecuteAsync(symbol, hasOpenPosition, ct));
            var results = await Task.WhenAll(tasks);

            return CombineResults(results, symbol, hasOpenPosition);
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
        bool hasOpenPosition)
    {
        if (results.Count == 0)
            return Hold(symbol, "No indicator results");

        // =========================
        // Extract indicator states
        // =========================

        var cci = results.FirstOrDefault(r => r.CciSignal != null);
        var macd = results.FirstOrDefault(r => r.MacdSignal != null);
        var ema = results.FirstOrDefault(r => r.Ema20Signal != null);

        bool cciAbove =
            cci?.CciSignal == "ABOVE_THRESHOLD";

        bool cciBelow =
            cci?.CciSignal == "BELOW_THRESHOLD";

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
        {
            // ---------- ENTRY ----------
            if (cciAbove && macdDarkGreen && aboveEma20)
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
                    $"MACD={(macdDarkGreen ? "DarkGreen" : "NotDarkGreen")}, " +
                    $"EMA20={(aboveEma20 ? "Above" : "Below")}";
            }
        }
        else
        {
            // ---------- EXIT ----------
            if (cciBelow)
            {
                action = AlgoAction.Sell;
                summary = "SELL: CCI dropped below 100";
            }
            else
            {
                action = AlgoAction.Hold;
                summary = "HOLD (in position): CCI still above 100";
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
}
