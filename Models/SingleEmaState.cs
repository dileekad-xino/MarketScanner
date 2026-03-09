namespace MarketScanner.Models;

/// <summary>
/// Stores the state of a single-period EMA calculation for a symbol/interval.
/// Supports real-time monitoring with committed and preview values.
/// </summary>
public class SingleEmaState
{
    public string Symbol { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;
    public int Period { get; set; }

    public double CommittedEma { get; set; }
    public double? PreviewEma { get; set; }
    public double? PreviousEma { get; set; }
    public double? PreviousCommittedEma { get; set; }

    public double LastPrice { get; set; }
    public DateTime LastTimestamp { get; set; }

    public double CurrentEma => PreviewEma ?? CommittedEma;
}
