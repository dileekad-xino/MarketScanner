namespace MarketScanner.Models;

/// <summary>
/// Stores the current state of EMA calculations for incremental updates.
/// </summary>
public class EmaState
{
    /// <summary>
    /// Symbol this EMA state is for.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Interval (e.g., "1min", "30s").
    /// </summary>
    public string Interval { get; set; } = string.Empty;

    /// <summary>
    /// Current Fast EMA value (12-period by default).
    /// </summary>
    public decimal FastEma { get; set; }

    /// <summary>
    /// Current Slow EMA value (26-period by default).
    /// </summary>
    public decimal SlowEma { get; set; }

    /// <summary>
    /// Current Signal EMA value (9-period EMA of MACD line).
    /// </summary>
    public decimal SignalEma { get; set; }

    /// <summary>
    /// Current MACD line value (Fast EMA - Slow EMA).
    /// </summary>
    public decimal MacdLine { get; set; }

    /// <summary>
    /// Previous MACD line value (for crossover detection).
    /// </summary>
    public decimal PreviousMacdLine { get; set; }

    /// <summary>
    /// Previous Signal line value (for crossover detection).
    /// </summary>
    public decimal PreviousSignalLine { get; set; }

    /// <summary>
    /// Timestamp of the last candlestick used to calculate this state.
    /// </summary>
    public DateTime LastTimestamp { get; set; }

    /// <summary>
    /// Close price of the last processed candlestick (for detecting same-timestamp updates).
    /// </summary>
    public decimal? LastProcessedClose { get; set; }

    /// <summary>
    /// Number of prices processed so far (for initialization tracking).
    /// </summary>
    public int ProcessedCount { get; set; }

    /// <summary>
    /// Fast EMA period.
    /// </summary>
    public int FastPeriod { get; set; }

    /// <summary>
    /// Slow EMA period.
    /// </summary>
    public int SlowPeriod { get; set; }

    /// <summary>
    /// Signal EMA period.
    /// </summary>
    public int SignalPeriod { get; set; }

    /// <summary>
    /// Indicates if EMAs are fully initialized (past the initial SMA phase).
    /// </summary>
    public bool IsInitialized => ProcessedCount >= SlowPeriod;
}

