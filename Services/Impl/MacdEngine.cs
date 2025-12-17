using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

public enum MacdMode
{
    Candle, // authoritative, candle-close only
    Live    // tick-preview state
}

/// <summary>
/// Correct TradingView-accurate MACD engine with dual-mode support:
/// ✔ SMA seeding for historical
/// ✔ EMA updates only from PRICE
/// ✔ Signal EMA updated from MACD
/// ✔ Tracks previous MACD & Signal values
/// ✔ Separate states for candle-close (authoritative) vs live tick preview
/// </summary>
public class MacdEngine
{
    private readonly ConcurrentDictionary<(string Symbol, string Interval, MacdMode Mode), MacdState> _states = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval, MacdMode Mode), object> _locks = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval, MacdMode Mode), (int Fast, int Slow, int Signal)> _periods = new();

    private (string Symbol, string Interval, MacdMode Mode) Key(string symbol, string interval, MacdMode mode) =>
        (symbol, interval, mode);


    // ----------------------------------------
    //  HISTORICAL INITIALIZATION
    // ----------------------------------------
    public void Initialize(string symbol, string interval, IEnumerable<Candlestick> candles,
                           int fast = 12, int slow = 26, int signal = 9, MacdMode mode = MacdMode.Candle)
    {
        var candleList = candles.OrderBy(c => c.Timestamp).ToList();
        var closes = candleList.Select(c => (double)c.Close).ToList();

        if (closes.Count < slow + signal + 10)
            return;

        var key = Key(symbol, interval, mode);
        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            double fastEma = closes.Take(fast).Average();
            double slowEma = closes.Take(slow).Average();

            double alphaFast = 2.0 / (fast + 1.0);
            double alphaSlow = 2.0 / (slow + 1.0);

            var macdSeries = new List<double>();

            // EMA warmup
            for (int i = slow; i < closes.Count; i++)
            {
                fastEma += alphaFast * (closes[i] - fastEma);
                slowEma += alphaSlow * (closes[i] - slowEma);

                macdSeries.Add(fastEma - slowEma);
            }

            // Signal SMA seed
            double signalEma = macdSeries.Take(signal).Average();
            double alphaSignal = 2.0 / (signal + 1.0);

            // Continue true signal EMA
            for (int i = signal; i < macdSeries.Count; i++)
                signalEma += alphaSignal * (macdSeries[i] - signalEma);

            // Save state
            _states[key] = new MacdState
            {
                Symbol = symbol,
                Interval = interval,
                FastEma = fastEma,
                SlowEma = slowEma,
                Signal = signalEma,
                PreviousMacd = null,
                PreviousSignal = null,
                LastPrice = closes.Last(),
                LastTimestamp = candleList.Last().Timestamp
            };

            _periods[key] = (fast, slow, signal);
        }
    }


    // ----------------------------------------
    //  LIVE TICK UPDATE (mode-aware)
    // ----------------------------------------
    public void UpdateOnTick(string symbol, string interval, decimal price, DateTime timestamp, MacdMode mode = MacdMode.Candle)
    {
        var key = Key(symbol, interval, mode);
        
        if (!_states.TryGetValue(key, out var state))
            return;
        
        if (!_periods.TryGetValue(key, out var periods))
            return;
        
        var lockObj = _locks.GetOrAdd(key, _ => new object());
        
        lock (lockObj)
        {
            double p = (double)price;
            
            double alphaFast = 2.0 / (periods.Fast + 1.0);
            double alphaSlow = 2.0 / (periods.Slow + 1.0);
            double alphaSignal = 2.0 / (periods.Signal + 1.0);
            
            // Update EMAs from price only (same logic as finalized candle)
            if (!state.FastEma.HasValue || !state.SlowEma.HasValue || !state.Signal.HasValue)
                return; // State not properly initialized
            
            state.FastEma = state.FastEma.Value + alphaFast * (p - state.FastEma.Value);
            state.SlowEma = state.SlowEma.Value + alphaSlow * (p - state.SlowEma.Value);
            
            double macd = state.FastEma.Value - state.SlowEma.Value;
            
            // Signal EMA from MACD
            state.Signal = state.Signal.Value + alphaSignal * (macd - state.Signal.Value);
            
            state.LastPrice = p;
            state.LastTimestamp = timestamp;
        }
    }


    // ----------------------------------------
    //  FINALIZED CANDLE UPDATE (mode-aware)
    // ----------------------------------------
    public void UpdateOnFinalizedCandle(string symbol, string interval, decimal close, DateTime ts, MacdMode mode = MacdMode.Candle)
    {
        var key = Key(symbol, interval, mode);

        if (!_states.TryGetValue(key, out var state))
            return;

        if (!_periods.TryGetValue(key, out var periods))
            return;

        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            double p = (double)close;

            double alphaFast = 2.0 / (periods.Fast + 1.0);
            double alphaSlow = 2.0 / (periods.Slow + 1.0);
            double alphaSignal = 2.0 / (periods.Signal + 1.0);

            // Save previous values BEFORE updating
            state.PreviousMacd = state.Macd;
            state.PreviousSignal = state.Signal;

            // Update EMAs from price only
            if (!state.FastEma.HasValue || !state.SlowEma.HasValue || !state.Signal.HasValue)
                return; // State not properly initialized

            state.FastEma = state.FastEma.Value + alphaFast * (p - state.FastEma.Value);
            state.SlowEma = state.SlowEma.Value + alphaSlow * (p - state.SlowEma.Value);

            double macd = state.FastEma.Value - state.SlowEma.Value;

            // Signal EMA from MACD
            state.Signal = state.Signal.Value + alphaSignal * (macd - state.Signal.Value);
            
            // Update computed Macd property (via state.Macd getter)

            state.LastPrice = p;
            state.LastTimestamp = ts;
        }
    }

    public void UpdateOnFinalizedCandle(string symbol, string interval, Candlestick candle, MacdMode mode = MacdMode.Candle) =>
        UpdateOnFinalizedCandle(symbol, interval, candle.Close, candle.Timestamp, mode);


    // ----------------------------------------
    //  GET CURRENT MACD VALUES
    // ----------------------------------------
    public (double macd, double signal, double hist)? GetLastMacd(string symbol, string interval, MacdMode mode = MacdMode.Candle)
    {
        if (!_states.TryGetValue(Key(symbol, interval, mode), out var s))
            return null;

        double macd = s.FastEma.Value - s.SlowEma.Value;
        double hist = macd - s.Signal.Value;

        return (macd, s.Signal.Value, hist);
    }


    // ----------------------------------------
    //  GET PREVIOUS (needed for crossover detection)
    // ----------------------------------------
    public (double Macd, double Signal)? GetPreviousMacd(string symbol, string interval, MacdMode mode = MacdMode.Candle)
    {
        if (!_states.TryGetValue(Key(symbol, interval, mode), out var s))
            return null;

        if (s.PreviousMacd == null || s.PreviousSignal == null)
            return null;

        return (s.PreviousMacd.Value, s.PreviousSignal.Value);
    }


    public bool TryGetState(string symbol, string interval, out MacdState state, MacdMode mode = MacdMode.Candle)
    {
        return _states.TryGetValue(Key(symbol, interval, mode), out state!);
    }

    /// <summary>
    /// Synchronize state from one mode to another (e.g., authoritative candle -> live preview).
    /// </summary>
    public void SyncState(string symbol, string interval, MacdMode fromMode, MacdMode toMode)
    {
        var fromKey = Key(symbol, interval, fromMode);
        var toKey = Key(symbol, interval, toMode);

        if (!_states.TryGetValue(fromKey, out var fromState))
            return;
        if (!_periods.TryGetValue(fromKey, out var periods))
            return;

        var lockObj = _locks.GetOrAdd(toKey, _ => new object());

        lock (lockObj)
        {
            _states[toKey] = new MacdState
            {
                Symbol = symbol,
                Interval = interval,
                FastEma = fromState.FastEma,
                SlowEma = fromState.SlowEma,
                Signal = fromState.Signal,
                PreviousMacd = fromState.PreviousMacd,
                PreviousSignal = fromState.PreviousSignal,
                LastPrice = fromState.LastPrice,
                LastTimestamp = fromState.LastTimestamp
            };

            _periods[toKey] = periods;
        }
    }
}
