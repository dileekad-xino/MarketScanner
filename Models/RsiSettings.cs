namespace MarketScanner.Models;

/// <summary>
/// Persisted RSI configuration used by the RSI algorithm and settings dialog.
/// </summary>
public class RsiSettings
{
    public int Period { get; set; } = 14;
    public double Oversold { get; set; } = 30.0;
    public double Overbought { get; set; } = 70.0;
    public int HistoricalDays { get; set; } = 30;

    public static RsiSettings CreateDefaults() => new()
    {
        Period = 14,
        Oversold = 30.0,
        Overbought = 70.0,
        HistoricalDays = 30
    };

    public RsiSettings Clone() => new()
    {
        Period = Period,
        Oversold = Oversold,
        Overbought = Overbought,
        HistoricalDays = HistoricalDays
    };
}

