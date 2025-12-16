using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Reusable RSI engine for incremental RSI calculations.
/// Not yet integrated into RSIAlgoStrategy - created for future use.
/// </summary>
public class RsiEngine
{
    private readonly ConcurrentDictionary<(string Symbol, string Interval), RsiState> _states = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval), object> _locks = new();

    /// <summary>
    /// Initializes RSI state from historical candlesticks using Wilder's smoothing method.
    /// </summary>
    public void Initialize(string symbol, string interval, IEnumerable<Candlestick> candles, int period = 14)
    {
        var candleList = candles.OrderBy(c => c.Timestamp).ToList();
        if (candleList.Count < period + 1)
            return;

        var closes = candleList.Select(c => (double)c.Close).ToArray();
        var changes = new double[closes.Length - 1];
        for (int i = 0; i < changes.Length; i++)
        {
            changes[i] = closes[i + 1] - closes[i];
        }

        var gains = changes.Select(x => x > 0 ? x : 0).ToArray();
        var losses = changes.Select(x => x < 0 ? -x : 0).ToArray();

        double avgGain = gains.Take(period).Average();
        double avgLoss = losses.Take(period).Average();

        var key = (symbol, interval);
        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            // Apply Wilder's smoothing for remaining periods
            for (int i = period; i < changes.Length; i++)
            {
                avgGain = ((avgGain * (period - 1)) + gains[i]) / period;
                avgLoss = ((avgLoss * (period - 1)) + losses[i]) / period;
            }

            _states[key] = new RsiState
            {
                AvgGain = avgGain,
                AvgLoss = avgLoss,
                LastClose = closes.Last(),
                LastTimestamp = candleList.Last().Timestamp,
                Period = period,
                Symbol = symbol,
                Interval = interval
            };
        }
    }

    /// <summary>
    /// Updates RSI state with a live tick price using Wilder's smoothing method.
    /// </summary>
    public void UpdateLive(string symbol, string interval, decimal tickPrice, DateTime timestamp)
    {
        var key = (symbol, interval);
        if (!_states.TryGetValue(key, out var state))
            return;

        var lockObj = _locks.GetOrAdd(key, _ => new object());
        lock (lockObj)
        {
            double change = (double)tickPrice - state.LastClose;
            double gain = change > 0 ? change : 0;
            double loss = change < 0 ? -change : 0;

            // Wilder's smoothing: New Avg = [(Previous Avg × (Period - 1)) + Current Value] / Period
            state.AvgGain = ((state.AvgGain * (state.Period - 1)) + gain) / state.Period;
            state.AvgLoss = ((state.AvgLoss * (state.Period - 1)) + loss) / state.Period;

            state.LastClose = (double)tickPrice;
            state.LastTimestamp = timestamp;
        }
    }

    /// <summary>
    /// Gets the current RSI value for a symbol/interval.
    /// </summary>
    public double? GetRsi(string symbol, string interval)
    {
        var key = (symbol, interval);
        if (!_states.TryGetValue(key, out var state))
            return null;

        if (state.AvgLoss == 0)
            return 100.0;

        double rs = state.AvgGain / state.AvgLoss;
        return 100.0 - (100.0 / (1.0 + rs));
    }

    /// <summary>
    /// Tries to get the RSI state for a symbol/interval.
    /// </summary>
    public bool TryGetState(string symbol, string interval, out RsiState state)
    {
        var key = (symbol, interval);
        return _states.TryGetValue(key, out state!);
    }
}

