namespace MarketScanner.Models;

/// <summary>
/// Stores the state of MACD calculations for a symbol/interval.
/// Includes previous MACD/signal for crossover detection.
/// </summary>
public class MacdState
{
    public string Symbol { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;

    public double? FastEma { get; set; }
    public double? SlowEma { get; set; }
    public double? Signal { get; set; }

    public double LastPrice { get; set; }
    public DateTime LastTimestamp { get; set; }

    // NEW — needed for crossover detection
    public double? PreviousMacd { get; set; }
    public double? PreviousSignal { get; set; }

    // Computed MACD
    public double? Macd =>
        (FastEma.HasValue && SlowEma.HasValue)
            ? FastEma.Value - SlowEma.Value
            : null;
}
