namespace MarketScanner.Models;

/// <summary>
/// Represents MACD (Moving Average Convergence Divergence) indicator values.
/// </summary>
public sealed record MacdData(
    string Symbol,
    decimal MacdLine,
    decimal SignalLine,
    decimal Histogram,
    DateTime Timestamp,
    string Interval
)
{
    /// <summary>
    /// Indicates if MACD line is above signal line (bullish).
    /// </summary>
    public bool IsBullish => MacdLine > SignalLine;

    /// <summary>
    /// Indicates if MACD line is below signal line (bearish).
    /// </summary>
    public bool IsBearish => MacdLine < SignalLine;

    /// <summary>
    /// Indicates if histogram is positive.
    /// </summary>
    public bool HasPositiveHistogram => Histogram > 0;

    /// <summary>
    /// Indicates if histogram is negative.
    /// </summary>
    public bool HasNegativeHistogram => Histogram < 0;

    /// <summary>
    /// Gets the difference between MACD line and Signal line.
    /// </summary>
    public decimal Difference => MacdLine - SignalLine;

    /// <summary>
    /// Indicates if histogram growing
    /// </summary>
    public bool IsHistogramGrowing { get; init; }

}

