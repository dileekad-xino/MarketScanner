using MarketScanner.Models;
using MarketScanner.ViewModels;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Placeholder algorithm implementation for testing.
/// This can be replaced with real trading algorithms later.
/// </summary>
public class PlaceholderAlgoStrategy : IAlgoStrategy
{
    public string Name => "Placeholder Algorithm";
    public string Description => "A simple placeholder algorithm that demonstrates the algo runner interface.";

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        // Simulate some processing time
        await Task.Delay(500, ct);

        // Simple logic: Buy if price is positive and change is positive, otherwise Hold
        var action = symbol.LastPrice > 0 && symbol.ChangePercent > 0 
            ? AlgoAction.Buy 
            : AlgoAction.Hold;

        var reason = action == AlgoAction.Buy
            ? $"Price is ${symbol.LastPrice:F2} with positive change of {symbol.ChangePercent:F2}%"
            : "Waiting for better entry point or price movement.";

        return new AlgoResult(
            Symbol: symbol.Symbol,
            Action: action,
            Price: symbol.LastPrice,
            Reason: reason,
            Timestamp: DateTime.UtcNow
        );
    }
}

