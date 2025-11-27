namespace MarketScanner.Models;

/// <summary>
/// Represents an active RSI trade context for tracking 50-cross BUY entries
/// and monitoring for 60/70 exit conditions.
/// </summary>
public sealed class RsiTradeContext
{
    /// <summary>
    /// Symbol being tracked (e.g., "AAPL")
    /// </summary>
    public string Symbol { get; init; }

    /// <summary>
    /// Bar size / timeframe (e.g., "1 min", "5 min")
    /// </summary>
    public string Timeframe { get; init; }

    /// <summary>
    /// Timestamp when the 50-cross BUY signal was generated
    /// </summary>
    public DateTime EntryTimestamp { get; init; }

    /// <summary>
    /// RSI value at the time of entry
    /// </summary>
    public double EntryRsi { get; init; }

    /// <summary>
    /// Bar index at entry (for debugging)
    /// </summary>
    public int EntryBarIndex { get; init; }

    /// <summary>
    /// Highest RSI value reached after entry (for tracking rally strength)
    /// </summary>
    public double PeakRsi { get; set; }

    /// <summary>
    /// Whether RSI has gone above the TakeProfitLevel (60) since entry
    /// </summary>
    public bool ReachedTakeProfitLevel { get; set; }

    /// <summary>
    /// Whether RSI has gone above the Overbought level (70) since entry
    /// </summary>
    public bool ReachedOverbought { get; set; }

    public RsiTradeContext(string symbol, string timeframe, DateTime entryTimestamp, double entryRsi, int entryBarIndex)
    {
        Symbol = symbol;
        Timeframe = timeframe;
        EntryTimestamp = entryTimestamp;
        EntryRsi = entryRsi;
        EntryBarIndex = entryBarIndex;
        PeakRsi = entryRsi;
        ReachedTakeProfitLevel = false;
        ReachedOverbought = false;
    }

    /// <summary>
    /// Updates the peak RSI and tracking flags based on current RSI value
    /// </summary>
    public void UpdatePeak(double currentRsi, double takeProfitLevel, double overboughtLevel)
    {
        if (currentRsi > PeakRsi)
        {
            PeakRsi = currentRsi;
        }

        if (currentRsi >= takeProfitLevel)
        {
            ReachedTakeProfitLevel = true;
        }

        if (currentRsi >= overboughtLevel)
        {
            ReachedOverbought = true;
        }
    }
}

