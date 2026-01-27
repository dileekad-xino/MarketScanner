using MarketScanner.Models;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Composite algorithm strategy that combines multiple individual strategies.
/// </summary>
public class AlgoStrategy : IAlgoStrategy
{
    private readonly IReadOnlyList<IAlgoStrategy> _strategies;
    private readonly ILogger<AlgoStrategy> _logger;

    public string Name => "Composite Algorithm";
    public string Description => $"Combines {_strategies.Count} strategies: {string.Join(", ", _strategies.Select(s => s.Name))}";

    public AlgoStrategy(
        IEnumerable<IAlgoStrategy> strategies,
        ILogger<AlgoStrategy> logger)
    {
        _strategies = strategies.ToList();
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            // Execute all strategies in parallel
            var tasks = _strategies.Select(s => s.ExecuteAsync(symbol, ct));
            var results = await Task.WhenAll(tasks);

            // Combine results
            var combinedResult = CombineResults(results, symbol);

            return combinedResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composite strategy failed for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"Composite strategy error: {ex.Message}",
                Timestamp: DateTime.UtcNow
            );
        }
    }

    private AlgoResult CombineResults(IReadOnlyList<AlgoResult> results, ScannerRowViewModel symbol)
    {
        if (results.Count == 0)
        {
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: "No strategy results available",
                Timestamp: DateTime.UtcNow
            );
        }

        // Get average price from results, fallback to symbol price
        var prices = results.Where(r => r.Price.HasValue).Select(r => r.Price!.Value).ToList();
        var avgPrice = prices.Count > 0 ? prices.Average() : symbol.LastPrice;

        // Get MACD data from results (take first non-null)
        var macdData = results.FirstOrDefault(r => r.Macd != null)?.Macd;
        var crossover = results.FirstOrDefault(r => r.Crossover != CrossoverStatus.None)?.Crossover ?? CrossoverStatus.None;

        // Get RSI data from results (take first non-null) - FOR DISPLAY ONLY
        var rsiValue = results.FirstOrDefault(r => r.RsiValue.HasValue)?.RsiValue;
        var rsiSignal = results.FirstOrDefault(r => !string.IsNullOrEmpty(r.RsiSignal))?.RsiSignal;

        // Get CCI data from results (take first non-null)
        var cciValue = results.FirstOrDefault(r => r.CciValue.HasValue)?.CciValue;
        var cciSignal = results.FirstOrDefault(r => !string.IsNullOrEmpty(r.CciSignal))?.CciSignal;

        // Get EMA 20 data from results (take first non-null)
        var ema20Value = results.FirstOrDefault(r => r.Ema20Value.HasValue)?.Ema20Value;
        var ema20Signal = results.FirstOrDefault(r => !string.IsNullOrEmpty(r.Ema20Signal))?.Ema20Signal;

        // Extract individual strategy actions
        var macdResult = results.FirstOrDefault(r => r.Macd != null || r.Crossover != CrossoverStatus.None);
        var cciResult = results.FirstOrDefault(r => r.CciValue.HasValue || !string.IsNullOrEmpty(r.CciSignal));
        var ema20Result = results.FirstOrDefault(r => r.Ema20Value.HasValue || !string.IsNullOrEmpty(r.Ema20Signal));

        // Get actions from MACD, CCI, and EMA 20
        var macdAction = macdResult?.Action ?? AlgoAction.Hold;
        var cciAction = cciResult?.Action ?? AlgoAction.Hold;
        var ema20Action = ema20Result?.Action ?? AlgoAction.Hold;

        // Decision logic with CCI priority for SELL
        var action = AlgoAction.Hold;
        var summary = "";

        // BUY: Requires MACD, CCI, and EMA20 all to agree
        if (macdAction == AlgoAction.Buy && cciAction == AlgoAction.Buy && ema20Action == AlgoAction.Buy)
        {
            action = AlgoAction.Buy;
            summary = "BUY: MACD, CCI, and EMA20 all agree on BUY";
        }
        // SELL: CCI has priority (if CCI = SELL, final action = SELL, except when CCI = HOLD)
        else if (cciAction == AlgoAction.Sell)
        {
            action = AlgoAction.Sell;
            if (macdAction == AlgoAction.Sell)
            {
                summary = "SELL: MACD and CCI both agree on SELL (CCI priority)";
            }
            else
            {
                summary = $"SELL: CCI priority overrides MACD {macdAction}";
            }
        }
        // Exception: CCI = HOLD and MACD = SELL → HOLD
        else if (cciAction == AlgoAction.Hold && macdAction == AlgoAction.Sell)
        {
            action = AlgoAction.Hold;
            summary = "HOLD: CCI HOLD overrides MACD SELL";
        }
        // All other cases: HOLD
        else
        {
            action = AlgoAction.Hold;
            summary = $"HOLD: MACD={macdAction}, CCI={cciAction}, EMA20={ema20Action}";
        }

        var reasons = string.Join(" | ", new[] { macdResult, cciResult, ema20Result }
            .Where(r => r != null)
            .Select(r => $"[{r.Action}] {r.Reason}"));

        // Include RSI info in reason for reference (display only, not used in decision)
        var rsiInfo = rsiValue.HasValue ? $" | RSI: {rsiValue.Value:F2} ({rsiSignal ?? "N/A"}) [DISPLAY ONLY]" : "";

        return new AlgoResult(
            Symbol: symbol.Symbol,
            Action: action,
            Price: avgPrice,
            Reason: $"{summary} | {reasons}{rsiInfo}",
            Timestamp: DateTime.UtcNow,
            Macd: macdData,
            Crossover: crossover,
            RsiValue: rsiValue,
            RsiSignal: rsiSignal,
            CciValue: cciValue,
            CciSignal: cciSignal,
            Ema20Value: ema20Value,
            Ema20Signal: ema20Signal
        );
    }
}

