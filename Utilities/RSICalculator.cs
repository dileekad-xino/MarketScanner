using System.Linq;
using MarketScanner.Models;

namespace MarketScanner.Utilities;

/// <summary>
/// Utility class for calculating Relative Strength Index (RSI) from historical price data.
/// RSI is a momentum oscillator that measures the speed and magnitude of price changes.
/// </summary>
public static class RSICalculator
{
    /// <summary>
    /// Calculates RSI from historical close prices using the standard Wilder's smoothing method.
    /// </summary>
    /// <param name="closePrices">Array of close prices in chronological order (oldest to newest)</param>
    /// <param name="period">RSI period (default 14, standard for daily charts)</param>
    /// <returns>RSI value between 0 and 100</returns>
    /// <exception cref="ArgumentException">Thrown when insufficient data points are provided</exception>
    public static double Calculate(double[] closePrices, int period = 14)
    {
        if (closePrices == null || closePrices.Length == 0)
            throw new ArgumentException("Close prices array cannot be null or empty", nameof(closePrices));

        if (closePrices.Length < period + 1)
            throw new ArgumentException(
                $"Need at least {period + 1} data points for {period}-period RSI, but only {closePrices.Length} provided",
                nameof(closePrices));

        if (period <= 0)
            throw new ArgumentException("Period must be greater than 0", nameof(period));

        // Calculate price changes (differences between consecutive closes)
        var changes = new double[closePrices.Length - 1];
        for (int i = 0; i < changes.Length; i++)
        {
            changes[i] = closePrices[i + 1] - closePrices[i];
        }

        // Separate gains and losses
        // Gain = positive change (or 0 if negative)
        // Loss = absolute value of negative change (or 0 if positive)
        var gains = new double[changes.Length];
        var losses = new double[changes.Length];
        for (int i = 0; i < changes.Length; i++)
        {
            gains[i] = changes[i] > 0 ? changes[i] : 0;
            losses[i] = changes[i] < 0 ? Math.Abs(changes[i]) : 0;
        }

        // Calculate initial average gain and loss (simple average of first 'period' values)
        double avgGain = gains.Take(period).Average();
        double avgLoss = losses.Take(period).Average();

        // Calculate smoothed averages for remaining periods using Wilder's smoothing method
        // Formula: New Avg = [(Previous Avg × (Period - 1)) + Current Value] / Period
        for (int i = period; i < changes.Length; i++)
        {
            avgGain = ((avgGain * (period - 1)) + gains[i]) / period;
            avgLoss = ((avgLoss * (period - 1)) + losses[i]) / period;
        }

        // Calculate RS (Relative Strength) and RSI
        // RS = Average Gain / Average Loss
        // RSI = 100 - (100 / (1 + RS))
        if (avgLoss == 0)
        {
            // If there are no losses, RSI is 100 (perfect upward trend)
            return 100.0;
        }

        double rs = avgGain / avgLoss;
        double rsi = 100.0 - (100.0 / (1.0 + rs));

        return rsi;
    }

    /// <summary>
    /// Calculates RSI for the entire series and returns the RSI value for each point (aligned with close prices length - 1).
    /// Useful when signal logic needs to inspect previous RSI values.
    /// </summary>
    public static double[] CalculateSeries(double[] closePrices, int period = 14)
    {
        if (closePrices == null || closePrices.Length == 0)
            throw new ArgumentException("Close prices array cannot be null or empty", nameof(closePrices));

        if (closePrices.Length < period + 1)
            throw new ArgumentException(
                $"Need at least {period + 1} data points for {period}-period RSI, but only {closePrices.Length} provided",
                nameof(closePrices));

        if (period <= 0)
            throw new ArgumentException("Period must be greater than 0", nameof(period));

        var changes = new double[closePrices.Length - 1];
        for (int i = 0; i < changes.Length; i++)
        {
            changes[i] = closePrices[i + 1] - closePrices[i];
        }

        var gains = new double[changes.Length];
        var losses = new double[changes.Length];
        for (int i = 0; i < changes.Length; i++)
        {
            gains[i] = changes[i] > 0 ? changes[i] : 0;
            losses[i] = changes[i] < 0 ? Math.Abs(changes[i]) : 0;
        }

        var result = new double[changes.Length];

        double avgGain = gains.Take(period).Average();
        double avgLoss = losses.Take(period).Average();

        // First RSI value corresponds to index period
        result[period - 1] = avgLoss == 0 ? 100 : 100.0 - (100.0 / (1.0 + (avgGain / avgLoss)));

        for (int i = period; i < changes.Length; i++)
        {
            avgGain = ((avgGain * (period - 1)) + gains[i]) / period;
            avgLoss = ((avgLoss * (period - 1)) + losses[i]) / period;

            if (avgLoss == 0)
            {
                result[i] = 100;
            }
            else
            {
                var rs = avgGain / avgLoss;
                result[i] = 100 - (100 / (1 + rs));
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates RSI from a list of historical bars (e.g., from IBKR API).
    /// </summary>
    /// <param name="closePrices">List of close prices from historical bars (oldest to newest)</param>
    /// <param name="period">RSI period (default 14)</param>
    /// <returns>RSI value between 0 and 100</returns>
    public static double CalculateFromBars(IEnumerable<decimal> closePrices, int period = 14)
    {
        if (closePrices == null)
            throw new ArgumentNullException(nameof(closePrices));

        var prices = closePrices.Select(p => (double)p).ToArray();
        return Calculate(prices, period);
    }

    /// <summary>
    /// Calculates RSI from a list of candlesticks.
    /// Extracts close prices and calculates RSI using the standard Wilder's smoothing method.
    /// </summary>
    /// <param name="candlesticks">List of candlesticks in chronological order (oldest to newest)</param>
    /// <param name="period">RSI period (default 14)</param>
    /// <returns>RSI value between 0 and 100</returns>
    public static double CalculateFromCandlesticks(IEnumerable<Candlestick> candlesticks, int period = 14)
    {
        if (candlesticks == null)
            throw new ArgumentNullException(nameof(candlesticks));

        var closePrices = candlesticks.Select(c => (double)c.Close).ToArray();
        return Calculate(closePrices, period);
    }

    /// <summary>
    /// Calculates RSI series from a list of candlesticks.
    /// Returns RSI value for each point (aligned with candlesticks length - 1).
    /// </summary>
    /// <param name="candlesticks">List of candlesticks in chronological order (oldest to newest)</param>
    /// <param name="period">RSI period (default 14)</param>
    /// <returns>Array of RSI values</returns>
    public static double[] CalculateSeriesFromCandlesticks(IEnumerable<Candlestick> candlesticks, int period = 14)
    {
        if (candlesticks == null)
            throw new ArgumentNullException(nameof(candlesticks));

        var closePrices = candlesticks.Select(c => (double)c.Close).ToArray();
        return CalculateSeries(closePrices, period);
    }

    /// <summary>
    /// Calculates multiple RSI values for different periods from the same dataset.
    /// Useful for comparing short-term vs long-term momentum.
    /// </summary>
    /// <param name="closePrices">Array of close prices</param>
    /// <param name="periods">Array of RSI periods to calculate</param>
    /// <returns>Dictionary mapping period to RSI value</returns>
    public static Dictionary<int, double> CalculateMultiple(double[] closePrices, int[] periods)
    {
        if (periods == null || periods.Length == 0)
            throw new ArgumentException("Periods array cannot be null or empty", nameof(periods));

        var results = new Dictionary<int, double>();
        foreach (var period in periods)
        {
            try
            {
                results[period] = Calculate(closePrices, period);
            }
            catch (ArgumentException)
            {
                // Skip periods that require more data than available
                continue;
            }
        }

        return results;
    }
}

