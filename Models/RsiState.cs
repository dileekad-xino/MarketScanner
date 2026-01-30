namespace MarketScanner.Models;

/// <summary>
/// Stores the state of RSI calculations for a symbol/interval.
/// Includes committed (candle-close) state and preview (tick-based) state.
/// Matches TradingView-style behavior: committed updates on candle close, preview provides intrabar updates.
/// </summary>
public class RsiState
{
    public string Symbol { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;

    // Committed state (candle-close, matches TradingView long-run)
    public double CommittedAvgGain { get; set; }
    public double CommittedAvgLoss { get; set; }
    public double CommittedLastClose { get; set; }
    public DateTime CommittedLastTimestamp { get; set; }

    // Preview state (tick-based, for UI display - does not modify committed state)
    public double? PreviewAvgGain { get; set; }
    public double? PreviewAvgLoss { get; set; }
    public double? PreviewLastClose { get; set; }

    // Previous preview value (for crossover detection if needed)
    public double? PreviousRsi { get; set; }

    public int Period { get; set; }

    // Computed committed RSI (authoritative, matches TradingView)
    public double CommittedRsi
    {
        get
        {
            if (CommittedAvgLoss == 0) return 100.0;  // All gains, no losses
            if (CommittedAvgGain == 0) return 0.0;    // All losses, no gains
            double rs = CommittedAvgGain / CommittedAvgLoss;
            return 100.0 - (100.0 / (1.0 + rs));
        }
    }

    // Computed preview RSI (intrabar preview)
    public double? PreviewRsi
    {
        get
        {
            if (!PreviewAvgGain.HasValue || !PreviewAvgLoss.HasValue)
                return null;
            if (PreviewAvgLoss.Value == 0) return 100.0;  // All gains, no losses
            if (PreviewAvgGain.Value == 0) return 0.0;    // All losses, no gains
            double rs = PreviewAvgGain.Value / PreviewAvgLoss.Value;
            return 100.0 - (100.0 / (1.0 + rs));
        }
    }

    // Backward compatibility: returns preview if available, otherwise committed
    [Obsolete("Use CommittedRsi or PreviewRsi properties instead")]
    public double AvgGain => CommittedAvgGain;
    
    [Obsolete("Use CommittedAvgGain or PreviewAvgGain properties instead")]
    public double AvgLoss => CommittedAvgLoss;
    
    [Obsolete("Use CommittedLastClose or PreviewLastClose properties instead")]
    public double LastClose => CommittedLastClose;
    
    [Obsolete("Use CommittedLastTimestamp instead")]
    public DateTime LastTimestamp => CommittedLastTimestamp;
}

