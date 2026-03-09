using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Generic EMA engine with TradingView-style behavior.
/// </summary>
public class EmaEngine
{
    private readonly ConcurrentDictionary<(string Symbol, string Interval, int Period), SingleEmaState> _states = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval, int Period), object> _locks = new();

    private (string Symbol, string Interval, int Period) Key(string symbol, string interval, int period) =>
        (symbol, interval, period);

    public void Initialize(string symbol, string interval, IEnumerable<Candlestick> candles, int period)
    {
        var candleList = candles.OrderBy(c => c.Timestamp).ToList();
        var closes = candleList.Select(c => (double)c.Close).ToList();

        if (closes.Count < period)
            return;

        var key = Key(symbol, interval, period);
        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            var alpha = 2.0 / (period + 1.0);
            double ema = closes.Take(period).Average();
            double? previousCommittedEma = null;

            for (int i = period; i < closes.Count; i++)
            {
                previousCommittedEma = ema;
                ema = ema + alpha * (closes[i] - ema);
            }

            _states[key] = new SingleEmaState
            {
                Symbol = symbol,
                Interval = interval,
                Period = period,
                CommittedEma = ema,
                PreviewEma = null,
                PreviousEma = null,
                PreviousCommittedEma = previousCommittedEma,
                LastPrice = closes.Last(),
                LastTimestamp = candleList.Last().Timestamp
            };
        }
    }

    public void UpdateOnBar(string symbol, string interval, int period, decimal close, DateTime timestamp)
    {
        var key = Key(symbol, interval, period);

        if (!_states.TryGetValue(key, out var state))
            return;

        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            double p = (double)close;
            double alpha = 2.0 / (period + 1.0);

            state.PreviousEma = state.PreviewEma;
            var previewEma = state.CommittedEma + alpha * (p - state.CommittedEma);

            state.PreviewEma = previewEma;
            state.LastPrice = p;
            state.LastTimestamp = timestamp;
        }
    }

    public void UpdateOnFinalizedCandle(string symbol, string interval, int period, decimal close, DateTime ts)
    {
        var key = Key(symbol, interval, period);

        if (!_states.TryGetValue(key, out var state))
            return;

        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            double p = (double)close;
            double alpha = 2.0 / (period + 1.0);

            state.PreviousCommittedEma = state.CommittedEma;
            state.CommittedEma = state.CommittedEma + alpha * (p - state.CommittedEma);

            state.PreviewEma = state.CommittedEma;
            state.PreviousEma = null;

            state.LastPrice = p;
            state.LastTimestamp = ts;
        }
    }

    public double? GetEma(string symbol, string interval, int period)
    {
        if (!_states.TryGetValue(Key(symbol, interval, period), out var state))
            return null;

        return state.PreviewEma ?? state.CommittedEma;
    }

    public (double? current, double? previous) GetEmaWithPrevious(string symbol, string interval, int period)
    {
        if (!_states.TryGetValue(Key(symbol, interval, period), out var state))
            return (null, null);

        var current = state.PreviewEma ?? state.CommittedEma;
        var previous = state.PreviewEma.HasValue
            ? state.CommittedEma
            : state.PreviousCommittedEma;

        return (current, previous);
    }

    public bool TryGetState(string symbol, string interval, int period, out SingleEmaState state)
    {
        return _states.TryGetValue(Key(symbol, interval, period), out state!);
    }
}
