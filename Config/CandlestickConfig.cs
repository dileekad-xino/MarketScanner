namespace MarketScanner.Config;

/// <summary>
/// Configuration for candlestick building and MACD calculation.
/// </summary>
public class CandlestickConfig
{
    /// <summary>
    /// Time interval in seconds for building candlesticks (default: 30).
    /// Supported values: 15, 30, 60 (1 minute).
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of candlesticks to store per symbol (rolling window).
    /// </summary>
    public int MaxCandlesticksToStore { get; set; } = 200;

    /// <summary>
    /// MACD calculation parameters.
    /// </summary>
    public MacdConfig Macd { get; set; } = new();
}

/// <summary>
/// MACD indicator configuration parameters.
/// </summary>
public class MacdConfig
{
    /// <summary>
    /// Fast EMA period for MACD calculation (default: 12).
    /// </summary>
    public int FastPeriod { get; set; } = 12;

    /// <summary>
    /// Slow EMA period for MACD calculation (default: 26).
    /// </summary>
    public int SlowPeriod { get; set; } = 26;

    /// <summary>
    /// Signal line EMA period for MACD calculation (default: 9).
    /// </summary>
    public int SignalPeriod { get; set; } = 9;
}

