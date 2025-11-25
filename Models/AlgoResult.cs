namespace MarketScanner.Models;

/// <summary>
/// Represents the action recommended by an algorithm.
/// </summary>
public enum AlgoAction
{
    Buy,
    Sell,
    Hold
}

/// <summary>
/// Represents the result of running an algorithm on a symbol.
/// </summary>
public sealed record AlgoResult(
    string Symbol,
    AlgoAction Action,
    double? Price,
    string? Reason,
    DateTime Timestamp,
    double? RsiValue = null,
    string? RsiSignal = null
);

