namespace MarketScanner.Models;

/// <summary>
/// Persisted RSI configuration used by the RSI algorithm and settings dialog.
/// </summary>
public class RsiSettings
{
    public int Period { get; set; } = 14;  // Default for 1m, but will be auto-adjusted based on timeframe
    public double Oversold { get; set; } = 40.0;  // Updated to match professional intraday logic
    public double Overbought { get; set; } = 75.0;  // Updated to match professional intraday logic
    public int HistoricalDays { get; set; } = 2;
    
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
        Period = 14,  // Default for 1m, but will be auto-adjusted based on timeframe
        Oversold = 40.0,  // Updated to match professional intraday logic
        Overbought = 75.0,  // Updated to match professional intraday logic
        HistoricalDays = 2,
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
        TrailingStopMode = TrailingStopMode,
        TrailingStopDistance = TrailingStopDistance,
        TrailingStopActivationPercent = TrailingStopActivationPercent,
        InitialStopLossPercent = InitialStopLossPercent,
        TrailingStopPoints = TrailingStopPoints // Deprecated
    };
}

