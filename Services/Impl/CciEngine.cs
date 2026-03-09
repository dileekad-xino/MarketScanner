using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// CCI engine with TradingView-style behavior.
/// </summary>
public class CciEngine
{
    private readonly ConcurrentDictionary<(string Symbol, string Interval), CciState> _states = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval), object> _locks = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval), int> _periods = new();

    private (string Symbol, string Interval) Key(string symbol, string interval) =>
        (symbol, interval);

    private static double CalculateTypicalPrice(Candlestick candle) =>
        (double)((candle.High + candle.Low + candle.Close) / 3.0m);

    private static double CalculateTypicalPrice(decimal high, decimal low, decimal close) =>
        (double)((high + low + close) / 3.0m);

    private static double CalculateSma(List<double> values, int period)
    {
        if (values.Count < period) return 0.0;
        var sum = values.Skip(values.Count - period).Sum();
        return sum / period;
    }

    private static double CalculateMeanDeviation(List<double> typicalPrices, int period, double sma)
    {
        if (typicalPrices.Count < period) return 0.0;
        var recentPrices = typicalPrices.Skip(typicalPrices.Count - period).ToList();
        var deviations = recentPrices.Select(tp => Math.Abs(tp - sma)).ToList();
        return deviations.Sum() / period;
    }

    private static double? CalculateCciFromTypicalPrices(List<double> typicalPrices, int period)
    {
        if (typicalPrices.Count < period)
            return null;

        var sma = CalculateSma(typicalPrices, period);
        var meanDeviation = CalculateMeanDeviation(typicalPrices, period, sma);
        if (meanDeviation == 0)
            return null;

        var lastTypicalPrice = typicalPrices[^1];
        return (lastTypicalPrice - sma) / (0.015 * meanDeviation);
    }

    public void Initialize(string symbol, string interval, IEnumerable<Candlestick> candles, int period = 14)
    {
        var candleList = candles.OrderBy(c => c.Timestamp).ToList();
        if (candleList.Count < period)
            return;

        var key = Key(symbol, interval);
        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            var typicalPrices = candleList.Select(CalculateTypicalPrice).ToList();
            var sma = CalculateSma(typicalPrices, period);
            var meanDeviation = CalculateMeanDeviation(typicalPrices, period, sma);

            double? previousCommittedCci = null;
            double? previousCommittedCci2 = null;

            if (typicalPrices.Count >= period + 1)
            {
                var previousTypicalPrices = typicalPrices.Take(typicalPrices.Count - 1).ToList();
                previousCommittedCci = CalculateCciFromTypicalPrices(previousTypicalPrices, period);

                if (previousTypicalPrices.Count >= period + 1)
                {
                    var previous2TypicalPrices = previousTypicalPrices.Take(previousTypicalPrices.Count - 1).ToList();
                    previousCommittedCci2 = CalculateCciFromTypicalPrices(previous2TypicalPrices, period);
                }
            }

            _states[key] = new CciState
            {
                Symbol = symbol,
                Interval = interval,
                CommittedSma = sma,
                CommittedMeanDeviation = meanDeviation,
                CommittedLastTypicalPrice = typicalPrices.Last(),
                CommittedLastTimestamp = candleList.Last().Timestamp,
                CommittedTypicalPrices = typicalPrices,
                Period = period,
                PreviewSma = null,
                PreviewMeanDeviation = null,
                PreviewLastTypicalPrice = null,
                PreviewTypicalPrices = null,
                PreviousCci = null,
                PreviousCommittedCci = previousCommittedCci,
                PreviousCommittedCci2 = previousCommittedCci2,
                EntryCciValue = null,
                ExitCciValue = null
            };

            _periods[key] = period;
        }
    }

    public void UpdateOnBar(string symbol, string interval, decimal high, decimal low, decimal close, DateTime timestamp)
    {
        var key = Key(symbol, interval);

        if (!_states.TryGetValue(key, out var state))
            return;

        if (!_periods.TryGetValue(key, out var period))
            return;

        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            state.PreviousCci = state.PreviewCci;

            double typicalPrice = CalculateTypicalPrice(high, low, close);

            var previewTypicalPrices = new List<double>(state.CommittedTypicalPrices);
            if (previewTypicalPrices.Count >= period)
            {
                previewTypicalPrices.RemoveAt(0);
            }
            previewTypicalPrices.Add(typicalPrice);

            double previewSma = CalculateSma(previewTypicalPrices, period);
            double previewMeanDeviation = CalculateMeanDeviation(previewTypicalPrices, period, previewSma);

            state.PreviewSma = previewSma;
            state.PreviewMeanDeviation = previewMeanDeviation;
            state.PreviewLastTypicalPrice = typicalPrice;
            state.PreviewTypicalPrices = previewTypicalPrices;
        }
    }

    public void UpdateOnFinalizedCandle(string symbol, string interval, decimal high, decimal low, decimal close, DateTime ts)
    {
        var key = Key(symbol, interval);

        if (!_states.TryGetValue(key, out var state))
            return;

        if (!_periods.TryGetValue(key, out var period))
            return;

        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            state.PreviousCommittedCci2 = state.PreviousCommittedCci;
            state.PreviousCommittedCci = state.CommittedCci;

            double typicalPrice = CalculateTypicalPrice(high, low, close);
            state.CommittedTypicalPrices.Add(typicalPrice);

            if (state.CommittedTypicalPrices.Count > period * 2)
            {
                state.CommittedTypicalPrices.RemoveAt(0);
            }

            state.CommittedSma = CalculateSma(state.CommittedTypicalPrices, period);
            state.CommittedMeanDeviation = CalculateMeanDeviation(state.CommittedTypicalPrices, period, state.CommittedSma);
            state.CommittedLastTypicalPrice = typicalPrice;
            state.CommittedLastTimestamp = ts;

            state.PreviewSma = state.CommittedSma;
            state.PreviewMeanDeviation = state.CommittedMeanDeviation;
            state.PreviewLastTypicalPrice = typicalPrice;
            state.PreviewTypicalPrices = new List<double>(state.CommittedTypicalPrices);
        }
    }

    public double? GetCci(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return null;

        if (s.PreviewCci.HasValue)
            return s.PreviewCci.Value;

        return s.CommittedCci;
    }

    public (double CommittedCci, double? PreviewCci)? GetBothCci(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return null;

        return (s.CommittedCci, s.PreviewCci);
    }

    public (double? current, double? previous) GetCciWithPrevious(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return (null, null);

        double? current = s.PreviewCci ?? s.CommittedCci;
        double? previous = s.PreviousCommittedCci;

        return (current, previous);
    }

    public (double? current, double? previous, double? previous2) GetCciWithHistory(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return (null, null, null);

        double? current = s.PreviewCci ?? s.CommittedCci;
        return (current, s.PreviousCommittedCci, s.PreviousCommittedCci2);
    }

    public bool TryGetState(string symbol, string interval, out CciState state)
    {
        return _states.TryGetValue(Key(symbol, interval), out state!);
    }
}
