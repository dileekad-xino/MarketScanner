namespace MarketScanner.Models;

/// <summary>
/// Persisted CCI configuration used by the CCI algorithm and settings dialog.
/// </summary>
public class CciSettings
{
    public int Period { get; set; } = 14;  // Default for 1m timeframe
    public double Overbought { get; set; } = 100.0;  // Standard overbought level
    public double Oversold { get; set; } = -100.0;  // Standard oversold level
    public int HistoricalDays { get; set; } = 2;

    public static CciSettings CreateDefaults() => new()
    {
        Period = 14,
        Overbought = 100.0,
        Oversold = -100.0,
        HistoricalDays = 2
    };

    public CciSettings Clone() => new()
    {
        Period = Period,
        Overbought = Overbought,
        Oversold = Oversold,
        HistoricalDays = HistoricalDays
    };
}

