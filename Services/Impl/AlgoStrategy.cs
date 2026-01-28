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

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, bool hasOpenPosition = false, CancellationToken ct = default)
    {
        try
        {
            // Execute all strategies in parallel
            var tasks = _strategies.Select(s => s.ExecuteAsync(symbol, hasOpenPosition, ct));
            var results = await Task.WhenAll(tasks);

            // Combine results with position-aware logic
            var combinedResult = CombineResults(results, symbol, hasOpenPosition);

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

    private AlgoResult CombineResults(IReadOnlyList<AlgoResult> results, ScannerRowViewModel symbol, bool hasOpenPosition)
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

        // Position-aware decision: only Buy when flat (all agree Buy), only Sell when in position (all agree Sell)
        var action = AlgoAction.Hold;
        var summary = "";

        if (hasOpenPosition)
        {
            // In position: only allow Sell when MACD, CCI, and EMA20 all agree on Sell; otherwise Hold; never Buy
            if (macdAction == AlgoAction.Sell && cciAction == AlgoAction.Sell && ema20Action == AlgoAction.Sell)
            {
                action = AlgoAction.Sell;
                summary = "SELL: In position; MACD, CCI, and EMA20 all agree on SELL";
            }
            else
            {
                action = AlgoAction.Hold;
                summary = "HOLD: In position; no full SELL agreement";
            }
        }
        else
        {
            // No position: only allow Buy when MACD, CCI, and EMA20 all agree on Buy; otherwise Hold; never Sell
            if (macdAction == AlgoAction.Buy && cciAction == AlgoAction.Buy && ema20Action == AlgoAction.Buy)
            {
                action = AlgoAction.Buy;
                summary = "BUY: MACD, CCI, and EMA20 all agree on BUY";
            }
            else
            {
                action = AlgoAction.Hold;
                // Display: when no position, show Hold for any Sell (we don't act on sell)
                var displayMacd = macdAction == AlgoAction.Sell ? AlgoAction.Hold : macdAction;
                var displayCci = cciAction == AlgoAction.Sell ? AlgoAction.Hold : cciAction;
                var displayEma20 = ema20Action == AlgoAction.Sell ? AlgoAction.Hold : ema20Action;
                summary = $"HOLD: MACD={displayMacd}, CCI={displayCci}, EMA20={displayEma20}";
            }
        }

        // Position-aware display: show Hold for Sell when no position (we don't act on sell); show Hold for Buy when in position (we don't act on buy)
        var reasons = string.Join(" | ", new[] { macdResult, cciResult, ema20Result }
            .Where(r => r != null)
            .Select(r =>
            {
                var displayAction = (r!.Action == AlgoAction.Buy && hasOpenPosition) || (r.Action == AlgoAction.Sell && !hasOpenPosition)
                    ? AlgoAction.Hold
                    : r.Action;
                return $"[{displayAction}] {r.Reason}";
            }));

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

