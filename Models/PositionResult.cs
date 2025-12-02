namespace MarketScanner.Models;

/// <summary>
/// Represents a completed trade result from position tracking.
/// Contains entry, exit, and performance metrics.
/// </summary>
public sealed record PositionResult(
    string PositionId,
    string Symbol,
    
    // Entry details
    string EntrySignal,
    double EntryPrice,
    double EntryRsi,
    DateTime EntryTimestamp,
    
    // Exit details
    string ExitSignal,
    double ExitPrice,
    double ExitRsi,
    DateTime ExitTimestamp,
    string ExitReason,
    
    // Performance metrics
    double PeakPrice,
    double TroughPrice,
    double PeakRsi,
    double TroughRsi,
    double RealizedPnLPercent,
    TimeSpan Duration,
    int CheckCount
)
{
    /// <summary>
    /// Creates a PositionResult from a closed PositionTracker
    /// </summary>
    public static PositionResult FromTracker(PositionTracker tracker)
    {
        if (tracker.IsOpen || tracker.ExitPrice == null || tracker.ExitRsi == null || tracker.ExitTimestamp == null)
        {
            throw new InvalidOperationException("PositionTracker must be closed to create PositionResult");
        }

        var realizedPnL = ((tracker.ExitPrice.Value - tracker.EntryPrice) / tracker.EntryPrice) * 100.0;
        var duration = tracker.ExitTimestamp.Value - tracker.EntryTimestamp;

        return new PositionResult(
            PositionId: tracker.PositionId,
            Symbol: tracker.Symbol,
            EntrySignal: tracker.EntrySignal,
            EntryPrice: tracker.EntryPrice,
            EntryRsi: tracker.EntryRsi,
            EntryTimestamp: tracker.EntryTimestamp,
            ExitSignal: tracker.ExitSignal!,
            ExitPrice: tracker.ExitPrice.Value,
            ExitRsi: tracker.ExitRsi.Value,
            ExitTimestamp: tracker.ExitTimestamp.Value,
            ExitReason: tracker.ExitReason!,
            PeakPrice: tracker.PeakPrice,
            TroughPrice: tracker.TroughPrice,
            PeakRsi: tracker.PeakRsi,
            TroughRsi: tracker.TroughRsi,
            RealizedPnLPercent: realizedPnL,
            Duration: duration,
            CheckCount: tracker.CheckCount
        );
    }

    /// <summary>
    /// Gets a human-readable summary of the trade
    /// </summary>
    public string GetSummary()
    {
        var durationStr = Duration.TotalMinutes < 1 
            ? $"{Duration.TotalSeconds:F0}s"
            : $"{Duration.TotalMinutes:F1}m";
        
        var pnlColor = RealizedPnLPercent >= 0 ? "✅" : "❌";
        
        return $"{pnlColor} {Symbol}: {EntrySignal} @ ${EntryPrice:F2} → {ExitSignal} @ ${ExitPrice:F2} " +
               $"({RealizedPnLPercent:+#0.00;-#0.00;0.00}%) | Duration: {durationStr} | " +
               $"Peak: ${PeakPrice:F2} ({PeakRsi:F1} RSI)";
    }
}

