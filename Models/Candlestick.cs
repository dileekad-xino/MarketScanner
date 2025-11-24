namespace MarketScanner.Models;

/// <summary>
/// Represents a candlestick (OHLCV) for a specific time period.
/// </summary>
public sealed record Candlestick(
    string Symbol,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    DateTime Timestamp,
    string Interval
)
{
    /// <summary>
    /// Indicates if the candlestick is bullish (close > open).
    /// </summary>
    public bool IsBullish => Close > Open;

    /// <summary>
    /// Indicates if the candlestick is bearish (close < open).
    /// </summary>
    public bool IsBearish => Close < Open;

    /// <summary>
    /// Gets the body size (absolute difference between open and close).
    /// </summary>
    public decimal BodySize => Math.Abs(Close - Open);

    /// <summary>
    /// Gets the upper wick size (high - max(open, close)).
    /// </summary>
    public decimal UpperWick => High - Math.Max(Open, Close);

    /// <summary>
    /// Gets the lower wick size (min(open, close) - low).
    /// </summary>
    public decimal LowerWick => Math.Min(Open, Close) - Low;

    /// <summary>
    /// Gets the total range (high - low).
    /// </summary>
    public decimal Range => High - Low;
}

