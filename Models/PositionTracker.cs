namespace MarketScanner.Models;

/// <summary>
/// Tracks an open trading position from entry signal to exit.
/// Used for simulated trade tracking and backtesting.
/// </summary>
public sealed class PositionTracker
{
    /// <summary>
    /// Unique identifier for this position
    /// </summary>
    public string PositionId { get; init; }

    /// <summary>
    /// Symbol being traded (e.g., "AAPL")
    /// </summary>
    public string Symbol { get; init; }

    /// <summary>
    /// Entry signal type (e.g., "STRONG BUY", "BUY", "50-cross")
    /// </summary>
    public string EntrySignal { get; init; }

    /// <summary>
    /// Entry price
    /// </summary>
    public double EntryPrice { get; init; }

    /// <summary>
    /// Entry RSI value
    /// </summary>
    public double EntryRsi { get; init; }

    /// <summary>
    /// Entry timestamp
    /// </summary>
    public DateTime EntryTimestamp { get; init; }

    /// <summary>
    /// Current price (updated on each check)
    /// </summary>
    public double CurrentPrice { get; set; }

    /// <summary>
    /// Current RSI value (updated on each check)
    /// </summary>
    public double CurrentRsi { get; set; }

    /// <summary>
    /// Highest price reached since entry
    /// </summary>
    public double PeakPrice { get; set; }

    /// <summary>
    /// Lowest price reached since entry
    /// </summary>
    public double TroughPrice { get; set; }

    /// <summary>
    /// Highest RSI reached since entry
    /// </summary>
    public double PeakRsi { get; set; }

    /// <summary>
    /// Lowest RSI reached since entry
    /// </summary>
    public double TroughRsi { get; set; }

    /// <summary>
    /// Number of monitoring cycles performed
    /// </summary>
    public int CheckCount { get; set; }

    /// <summary>
    /// Whether position is still open
    /// </summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>
    /// Exit signal type (set when position closes)
    /// </summary>
    public string? ExitSignal { get; set; }

    /// <summary>
    /// Exit price (set when position closes)
    /// </summary>
    public double? ExitPrice { get; set; }

    /// <summary>
    /// Exit RSI value (set when position closes)
    /// </summary>
    public double? ExitRsi { get; set; }

    /// <summary>
    /// Exit timestamp (set when position closes)
    /// </summary>
    public DateTime? ExitTimestamp { get; set; }

    /// <summary>
    /// Exit reason (set when position closes)
    /// </summary>
    public string? ExitReason { get; set; }

    public PositionTracker(
        string symbol,
        string entrySignal,
        double entryPrice,
        double entryRsi)
    {
        PositionId = Guid.NewGuid().ToString("N")[..8]; // Short ID for display
        Symbol = symbol;
        EntrySignal = entrySignal;
        EntryPrice = entryPrice;
        EntryRsi = entryRsi;
        EntryTimestamp = DateTime.UtcNow;
        CurrentPrice = entryPrice;
        CurrentRsi = entryRsi;
        PeakPrice = entryPrice;
        TroughPrice = entryPrice;
        PeakRsi = entryRsi;
        TroughRsi = entryRsi;
        CheckCount = 0;
    }

    /// <summary>
    /// Updates position with current market data
    /// </summary>
    public void Update(double currentPrice, double currentRsi)
    {
        if (!IsOpen) return;

        CurrentPrice = currentPrice;
        CurrentRsi = currentRsi;
        CheckCount++;

        // Update peaks and troughs
        if (currentPrice > PeakPrice) PeakPrice = currentPrice;
        if (currentPrice < TroughPrice) TroughPrice = currentPrice;
        if (currentRsi > PeakRsi) PeakRsi = currentRsi;
        if (currentRsi < TroughRsi) TroughRsi = currentRsi;
    }

    /// <summary>
    /// Closes the position with exit details
    /// </summary>
    public void Close(string exitSignal, double exitPrice, double exitRsi, string exitReason)
    {
        IsOpen = false;
        ExitSignal = exitSignal;
        ExitPrice = exitPrice;
        ExitRsi = exitRsi;
        ExitTimestamp = DateTime.UtcNow;
        ExitReason = exitReason;
    }

    /// <summary>
    /// Calculates unrealized profit/loss percentage
    /// </summary>
    public double GetUnrealizedPnLPercent()
    {
        if (!IsOpen) return 0;
        return ((CurrentPrice - EntryPrice) / EntryPrice) * 100.0;
    }

    /// <summary>
    /// Calculates realized profit/loss percentage (after exit)
    /// </summary>
    public double? GetRealizedPnLPercent()
    {
        if (IsOpen || ExitPrice == null) return null;
        return ((ExitPrice.Value - EntryPrice) / EntryPrice) * 100.0;
    }

    /// <summary>
    /// Gets position duration
    /// </summary>
    public TimeSpan GetDuration()
    {
        var endTime = ExitTimestamp ?? DateTime.UtcNow;
        return endTime - EntryTimestamp;
    }
}

