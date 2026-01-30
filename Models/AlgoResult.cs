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
/// Crossover status for MACD indicator.
/// </summary>
public enum CrossoverStatus
{
    None,
    CrossedUp,
    CrossedDown
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
    MacdData? Macd = null,
    CrossoverStatus Crossover = CrossoverStatus.None,
    double? RsiValue = null,
    string? RsiSignal = null,
    double? CciValue = null,
    string? CciSignal = null,
    double? Ema20Value = null,
    string? Ema20Signal = null
);

