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

        // Filter to only MACD and CCI strategies for decision making
        // Identify by indicator data presence
        var decisionResults = results.Where(r => 
            (r.Macd != null || r.Crossover != CrossoverStatus.None) ||  // MACD Strategy
            (r.CciValue.HasValue || !string.IsNullOrEmpty(r.CciSignal))   // CCI Strategy
        ).ToList();

        // Count actions only from MACD and CCI (decision-making strategies)
        var buyCount = decisionResults.Count(r => r.Action == AlgoAction.Buy);
        var sellCount = decisionResults.Count(r => r.Action == AlgoAction.Sell);
        var holdCount = decisionResults.Count(r => r.Action == AlgoAction.Hold);

        // Decision logic: require unanimous agreement between MACD and CCI only
        var total = decisionResults.Count;

        var action = AlgoAction.Hold;
        var reasons = string.Join(" | ", decisionResults.Select(r => $"[{r.Action}] {r.Reason}"));
        var summary = "";

        if (buyCount == total && total > 0)
        {
            action = AlgoAction.Buy;
            summary = $"BUY unanimous: MACD and CCI both agree on BUY ({buyCount}/{total})";
        }
        else if (sellCount == total && total > 0)
        {
            action = AlgoAction.Sell;
            summary = $"SELL unanimous: MACD and CCI both agree on SELL ({sellCount}/{total})";
        }
        else
        {
            summary = $"HOLD: MACD and CCI disagree (Buy: {buyCount}, Sell: {sellCount}, Hold: {holdCount}) - Unanimous agreement required";
        }

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
            CciSignal: cciSignal
        );
    }
}

