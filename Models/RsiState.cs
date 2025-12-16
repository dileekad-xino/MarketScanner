namespace MarketScanner.Models;

/// <summary>
/// Stores the current state of RSI calculations for incremental updates.
/// </summary>
public class RsiState
{
    public double AvgGain { get; set; }
    public double AvgLoss { get; set; }
    public double LastClose { get; set; }
    public DateTime LastTimestamp { get; set; }
    public int Period { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;
}

