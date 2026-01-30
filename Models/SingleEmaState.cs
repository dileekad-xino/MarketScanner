namespace MarketScanner.Models;

/// <summary>
/// Stores the state of a single-period EMA calculation for a symbol/interval.
/// Supports real-time monitoring with committed and preview values.
/// </summary>
public class SingleEmaState
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
    /// EMA period (e.g., 20, 50, 200).
    /// </summary>
    public int Period { get; set; }

    /// <summary>
    /// Committed EMA value (from finalized candles).
    /// </summary>
    public double CommittedEma { get; set; }

    /// <summary>
    /// Live/preview EMA value (from streaming bars, not committed).
    /// </summary>
    public double? PreviewEma { get; set; }

    /// <summary>
    /// Previous EMA value (for crossover detection if needed).
    /// </summary>
    public double? PreviousEma { get; set; }

    /// <summary>
    /// Last price processed.
    /// </summary>
    public double LastPrice { get; set; }

    /// <summary>
    /// Timestamp of the last candlestick/bar used to calculate this state.
    /// </summary>
    public DateTime LastTimestamp { get; set; }

    /// <summary>
    /// Gets the current EMA value (prefers preview, falls back to committed).
    /// </summary>
    public double CurrentEma => PreviewEma ?? CommittedEma;
}
