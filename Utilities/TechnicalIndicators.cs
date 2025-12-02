using MarketScanner.Models;

namespace MarketScanner.Utilities;

/// <summary>
/// Utility class for technical indicator calculations.
/// </summary>
public static class TechnicalIndicators
{
    /// <summary>
    /// Calculates MACD (Moving Average Convergence Divergence) from closing prices.
    /// </summary>
    /// <param name="closes">List of closing prices in chronological order (oldest to newest)</param>
    /// <param name="fastPeriod">Fast EMA period (default: 12)</param>
    /// <param name="slowPeriod">Slow EMA period (default: 26)</param>
    /// <param name="signalPeriod">Signal line EMA period (default: 9)</param>
    /// <returns>MACD data with MACD line, signal line, histogram, and validity flag</returns>
    public static MacdData? CalculateMacd(
        IReadOnlyList<decimal> closes,
        int fastPeriod = 12,
        int slowPeriod = 26,
        int signalPeriod = 9)
    {
        if (closes.Count < slowPeriod + signalPeriod)
            return null;

        var fastEma = CalculateEma(closes, fastPeriod);
        var slowEma = CalculateEma(closes, slowPeriod);

        // Calculate MACD line (fast EMA - slow EMA)
        // We need to wait until both EMAs have enough data points
        // The slow EMA needs slowPeriod values, so we start calculating at index slowPeriod - 1
        var macdLine = new List<decimal>();
        for (int i = 0; i < closes.Count; i++)
        {
            // Wait until slowPeriod - 1 to ensure slow EMA has enough data
            if (i >= slowPeriod - 1 && fastEma[i].HasValue && slowEma[i].HasValue)
            {
                macdLine.Add(fastEma[i].Value - slowEma[i].Value);
            }
            else
            {
                macdLine.Add(0);
            }
        }

        // Calculate signal line (EMA of MACD line)
        var signalLine = CalculateEma(macdLine, signalPeriod);

        // Calculate histogram (MACD - Signal)
        var histogram = new List<decimal?>();
        for (int i = 0; i < macdLine.Count; i++)
        {
            if (i >= signalPeriod - 1 && signalLine[i].HasValue)
            {
                histogram.Add(macdLine[i] - signalLine[i].Value);
            }
            else
            {
                histogram.Add(null);
            }
        }

        // Get the latest values - use Last() instead of LastOrDefault() to ensure we have valid data
        // We've already checked that we have enough data points, so Last() should be safe
        if (macdLine.Count == 0 || signalLine.Count == 0 || histogram.Count == 0)
            return null;

        var currentMacd = macdLine[macdLine.Count - 1]; // Get last element directly
        var currentSignal = signalLine[signalLine.Count - 1]; // Get last element directly
        var currentHistogram = histogram[histogram.Count - 1]; // Get last element directly

        if (!currentSignal.HasValue || currentHistogram == null)
            return null;

        // We need symbol and interval from the caller, but for now return a basic structure
        // The strategy will add symbol and interval
        return new MacdData(
            Symbol: "", // Will be set by caller
            MacdLine: currentMacd,
            SignalLine: currentSignal.Value,
            Histogram: currentHistogram.Value,
            Timestamp: DateTime.UtcNow,
            Interval: "" // Will be set by caller
        );
    }

    /// <summary>
    /// Calculates Exponential Moving Average (EMA).
    /// </summary>
    private static List<decimal?> CalculateEma(IReadOnlyList<decimal> prices, int period)
    {
        var ema = new List<decimal?>();
        var multiplier = 2.0m / (period + 1);

        for (int i = 0; i < prices.Count; i++)
        {
            if (i == 0)
            {
                ema.Add(prices[i]);
            }
            else if (i < period)
            {
                // Use SMA for initial values
                var sma = prices.Take(i + 1).Average();
                ema.Add(sma);
            }
            else
            {
                var prevEma = ema[i - 1].Value;
                ema.Add((prices[i] - prevEma) * multiplier + prevEma);
            }
        }

        return ema;
    }
}

