using System.Linq;

namespace MarketScanner.Utilities;

public static class MovingAverage
{
    public static double CalculateEma(double[] values, int period)
    {
        if (values == null || values.Length == 0)
            throw new ArgumentException("Values cannot be null or empty", nameof(values));
        if (period <= 0)
            throw new ArgumentException("Period must be greater than 0", nameof(period));

        double multiplier = 2d / (period + 1d);
        double ema = values.Take(period).Average();

        for (int i = period; i < values.Length; i++)
        {
            ema = ((values[i] - ema) * multiplier) + ema;
        }

        return ema;
    }
}

