namespace MarketScanner.Models;

/// <summary>
/// Persisted RSI configuration used by the RSI algorithm and settings dialog.
/// </summary>
public class RsiSettings
{
    public int Period { get; set; } = 14;
    public double Oversold { get; set; } = 35.0;
    public double Overbought { get; set; } = 65.0;
    public int HistoricalDays { get; set; } = 2;
    public string BarSize { get; set; } = "1 min"; // Supported: "15 secs", "30 secs", "1 min" (IBKR format)

    public static RsiSettings CreateDefaults() => new()
    {
        Period = 14,
        Oversold = 35.0,
        Overbought = 65.0,
        HistoricalDays = 2,
        BarSize = "1 min"
    };

    public RsiSettings Clone() => new()
    {
        Period = Period,
        Oversold = Oversold,
        Overbought = Overbought,
        HistoricalDays = HistoricalDays,
        BarSize = BarSize
    };
}

