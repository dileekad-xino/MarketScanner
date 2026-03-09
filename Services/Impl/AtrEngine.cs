using MarketScanner.Models;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Stateless ATR calculator helper.
/// </summary>
public class AtrEngine
{
    public double? CalculateAtr(IReadOnlyList<Candlestick> orderedCandles, int period)
    {
        if (orderedCandles == null || orderedCandles.Count < period + 1 || period < 2)
            return null;

        var trueRanges = new List<double>(orderedCandles.Count - 1);
        for (var i = 1; i < orderedCandles.Count; i++)
        {
            var current = orderedCandles[i];
            var previous = orderedCandles[i - 1];

            var highLow = (double)(current.High - current.Low);
            var highPrevClose = Math.Abs((double)(current.High - previous.Close));
            var lowPrevClose = Math.Abs((double)(current.Low - previous.Close));

            trueRanges.Add(Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose)));
        }

        if (trueRanges.Count < period)
            return null;

        double atr = trueRanges.Take(period).Average();
        for (var i = period; i < trueRanges.Count; i++)
        {
            atr = ((atr * (period - 1)) + trueRanges[i]) / period;
        }

        return atr;
    }
}
