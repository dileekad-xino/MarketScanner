using MarketScanner.Models;
using MarketScanner.ViewModels;

namespace MarketScanner.Services.Impl;

public class AlgoStrategy : IAlgoStrategy
{
    public string Name => "Algorithm";
    public string Description => "A simple algorithm that demonstrates the algo runner interface.";

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        await Task.Delay(500, ct);

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

