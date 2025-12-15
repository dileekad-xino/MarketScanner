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
    
    // Trailing Stop Configuration
    public TrailingStopMode TrailingStopMode { get; set; } = TrailingStopMode.Percentage;
    public double TrailingStopDistance { get; set; } = 1.0; // Percentage (1.0 = 1%) or Price amount
    public double TrailingStopActivationPercent { get; set; } = 2.0; // Activation price as % above entry (default 2%)
    
    // Risk Management
    public double InitialStopLossPercent { get; set; } = 2.0; // Initial stop-loss as % below entry (default 2%)
    
    // Deprecated: Use TrailingStopDistance instead. Kept for backward compatibility.
    [Obsolete("Use TrailingStopDistance instead")]
    public double TrailingStopPoints { get; set; } = 4.0;

    public static RsiSettings CreateDefaults() => new()
    {
        Period = 14,
        Oversold = 35.0,
        Overbought = 65.0,
        HistoricalDays = 2,
        BarSize = "1 min",
        TrailingStopMode = TrailingStopMode.Percentage,
        TrailingStopDistance = 1.0,
        TrailingStopActivationPercent = 2.0,
        InitialStopLossPercent = 2.0,
        TrailingStopPoints = 4.0 // Deprecated
    };

    public RsiSettings Clone() => new()
    {
        Period = Period,
        Oversold = Oversold,
        Overbought = Overbought,
        HistoricalDays = HistoricalDays,
        BarSize = BarSize,
        TrailingStopMode = TrailingStopMode,
        TrailingStopDistance = TrailingStopDistance,
        TrailingStopActivationPercent = TrailingStopActivationPercent,
        InitialStopLossPercent = InitialStopLossPercent,
        TrailingStopPoints = TrailingStopPoints // Deprecated
    };
}

