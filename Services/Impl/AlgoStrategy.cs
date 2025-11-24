using MarketScanner.Models;
using MarketScanner.ViewModels;

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

        // Count actions
        var buyCount = results.Count(r => r.Action == AlgoAction.Buy);
        var sellCount = results.Count(r => r.Action == AlgoAction.Sell);
        var holdCount = results.Count(r => r.Action == AlgoAction.Hold);

        // Get average price from results, fallback to symbol price
        var prices = results.Where(r => r.Price.HasValue).Select(r => r.Price!.Value).ToList();
        var avgPrice = prices.Count > 0 ? prices.Average() : symbol.LastPrice;

        // Decision logic: require majority consensus
        var total = results.Count;
        var buyRatio = (double)buyCount / total;
        var sellRatio = (double)sellCount / total;

        var action = AlgoAction.Hold;
        var reasons = string.Join(" | ", results.Select(r => $"[{r.Action}] {r.Reason}"));
        var summary = "";

        if (buyRatio >= 0.5)
        {
            action = AlgoAction.Buy;
            summary = $"BUY consensus: {buyCount}/{total} strategies recommend BUY";
        }
        else if (sellRatio >= 0.5)
        {
            action = AlgoAction.Sell;
            summary = $"SELL consensus: {sellCount}/{total} strategies recommend SELL";
        }
        else
        {
            summary = $"HOLD: Mixed signals (Buy: {buyCount}, Sell: {sellCount}, Hold: {holdCount})";
        }

        return new AlgoResult(
            Symbol: symbol.Symbol,
            Action: action,
            Price: avgPrice,
            Reason: $"{summary} | {reasons}",
            Timestamp: DateTime.UtcNow
        );
    }
}

