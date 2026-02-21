namespace MarketScanner.Models;

/// <summary>
/// Stores the state of CCI calculations for a symbol/interval.
/// Includes committed (candle-close) state and preview (tick-based) state.
/// Matches TradingView-style behavior: committed updates on candle close, preview provides intrabar updates.
/// </summary>
public class CciState
{
    public string Symbol { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;

    // Committed state (candle-close, matches TradingView long-run)
    public double CommittedSma { get; set; }  // Simple Moving Average of Typical Price
    public double CommittedMeanDeviation { get; set; }  // The Mean Deviation
    public double CommittedLastTypicalPrice { get; set; }  // Last Typical Price (H+L+C)/3
    public DateTime CommittedLastTimestamp { get; set; }
    
    // Historical typical prices for SMA and mean deviation calculation
    public List<double> CommittedTypicalPrices { get; set; } = new();

    // Preview state (tick-based, for UI display - does not modify committed state)
    public double? PreviewSma { get; set; }
    public double? PreviewMeanDeviation { get; set; }
    public double? PreviewLastTypicalPrice { get; set; }
    public List<double>? PreviewTypicalPrices { get; set; }

    // Previous preview value (for crossover detection if needed)
    public double? PreviousCci { get; set; }

    // Previous committed CCI value (for crossover detection on candle close)
    public double? PreviousCommittedCci { get; set; }

    // Entry/exit CCI values for momentum crossover strategy
    public double? EntryCciValue { get; set; }  // CCI value when BUY signal was generated (cross above +100)
    public double? ExitCciValue { get; set; }   // CCI value when SELL signal was generated (cross below +100)

    public int Period { get; set; }

    // Computed committed CCI (authoritative, matches TradingView)
    public double CommittedCci
    {
        get
        {
            if (CommittedMeanDeviation == 0) return 0.0;  // Avoid division by zero
            return (CommittedLastTypicalPrice - CommittedSma) / (0.015 * CommittedMeanDeviation);
        }
    }

    // Computed preview CCI (intrabar preview)
    public double? PreviewCci
    {
        get
        {
            if (!PreviewSma.HasValue || !PreviewMeanDeviation.HasValue || PreviewMeanDeviation.Value == 0)
                return null;
            return (PreviewLastTypicalPrice.Value - PreviewSma.Value) / (0.015 * PreviewMeanDeviation.Value);
        }
    }
}

