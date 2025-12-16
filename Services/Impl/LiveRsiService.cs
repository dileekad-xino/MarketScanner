using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Service for incremental RSI updates on live ticks.
/// Separate from RSIAlgoStrategy to preserve existing historical calculation logic.
/// </summary>
public class LiveRsiService
{
    private readonly ConcurrentDictionary<string, RsiState> _states = new();
    private readonly ConcurrentDictionary<string, object> _updateLocks = new();

    /// <summary>
    /// Initializes RSI state from historical candlesticks.
    /// Must be called before LiveUpdate can be used.
    /// </summary>
    public void Initialize(string symbol, string interval, IList<Candlestick> candles, int period)
    {
        if (candles == null || candles.Count < period + 1)
            return;

        var closes = candles.Select(c => (double)c.Close).ToArray();
        var changes = new double[closes.Length - 1];
        for (int i = 0; i < changes.Length; i++)
        {
            changes[i] = closes[i + 1] - closes[i];
        }

        var gains = changes.Select(x => x > 0 ? x : 0).ToArray();
        var losses = changes.Select(x => x < 0 ? -x : 0).ToArray();

        double avgGain = gains.Take(period).Average();
        double avgLoss = losses.Take(period).Average();

        var cacheKey = $"{symbol}_{interval}";
        _states[cacheKey] = new RsiState
        {
            AvgGain = avgGain,
            AvgLoss = avgLoss,
            LastClose = closes.Last(),
            LastTimestamp = candles.Last().Timestamp,
            Period = period,
            Symbol = symbol,
            Interval = interval
        };
    }

    /// <summary>
    /// Performs incremental RSI update using Wilder's smoothing method.
    /// Returns null if state not initialized.
    /// </summary>
    public double? LiveUpdate(string symbol, string interval, double lastPrice, DateTime timestamp)
    {
        var cacheKey = $"{symbol}_{interval}";
        if (!_states.TryGetValue(cacheKey, out var state))
            return null;

        var lockObj = _updateLocks.GetOrAdd(cacheKey, _ => new object());
        lock (lockObj)
        {
            double change = lastPrice - state.LastClose;
            double gain = change > 0 ? change : 0;
            double loss = change < 0 ? -change : 0;

            // Wilder's smoothing: New Avg = [(Previous Avg × (Period - 1)) + Current Value] / Period
            state.AvgGain = ((state.AvgGain * (state.Period - 1)) + gain) / state.Period;
            state.AvgLoss = ((state.AvgLoss * (state.Period - 1)) + loss) / state.Period;

            state.LastClose = lastPrice;
            state.LastTimestamp = timestamp;

            if (state.AvgLoss == 0)
                return 100.0;

            double rs = state.AvgGain / state.AvgLoss;
            return 100.0 - (100.0 / (1.0 + rs));
        }
    }
}

