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
    public int MaxCandlesticksToStore { get; set; } = 500;

    /// <summary>
    /// Number of historical candlesticks to preload for MACD calculation.
    /// More data = more stable MACD. Recommended: 300-500 for minute intervals.
    /// Default: 400 (approximately 1 trading day for 1-minute intervals).
    /// </summary>
    public int HistoricalPreloadCount { get; set; } = 400;

    /// <summary>
    /// Number of candlesticks to use for MACD calculation.
    /// Should be >= HistoricalPreloadCount. Using more data improves accuracy.
    /// Default: 400 (use all preloaded data).
    /// </summary>
    public int MacdCalculationWindow { get; set; } = 400;

    /// <summary>
    /// MACD calculation parameters.
    /// </summary>
    public MacdConfig Macd { get; set; } = new();

    public bool EnablePollingFallback { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 10;
    public int PollingBarsToFetch { get; set; } = 3;
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

