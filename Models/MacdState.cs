namespace MarketScanner.Models;

/// <summary>
/// Stores the state of MACD calculations for a symbol/interval.
/// Includes previous MACD/signal for crossover detection.
/// </summary>
public class MacdState
{
    public string Symbol { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;

    // Committed (candle-close) state
    public double FastEma { get; set; }
    public double SlowEma { get; set; }
    public double Signal { get; set; }

    public double LastPrice { get; set; }
    public DateTime LastTimestamp { get; set; }

    // Previous preview values (for tick crossover detection)
    public double? PreviousMacd { get; set; }
    public double? PreviousSignal { get; set; }

    // Latest tick preview (NOT committed)
    public double? LiveMacd { get; set; }
    public double? LiveSignal { get; set; }
    public double? LiveHist { get; set; }

    // Computed committed MACD
    public double Macd => FastEma - SlowEma;
}
