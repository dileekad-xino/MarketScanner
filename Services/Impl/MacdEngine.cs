using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// MACD engine with TradingView-style behavior:
/// - Committed OnFinalizedCandle() updates (authoritative, matches TV long-run)
/// - UpdateOnBar() provides intrabar preview from streaming bars WITHOUT compounding state (prevents drift)
/// - UpdateOnTick() is deprecated for MACD (kept for backward compatibility)
/// </summary>
public class MacdEngine
{
    private readonly ConcurrentDictionary<(string Symbol, string Interval), MacdState> _states = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval), object> _locks = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval), (int Fast, int Slow, int Signal)> _periods = new();

    private (string Symbol, string Interval) Key(string symbol, string interval) =>
        (symbol, interval);


    // ----------------------------------------
    //  HISTORICAL INITIALIZATION
    // ----------------------------------------
    public void Initialize(string symbol, string interval, IEnumerable<Candlestick> candles,
                           int fast = 12, int slow = 26, int signal = 9)
    {
        var candleList = candles.OrderBy(c => c.Timestamp).ToList();
        var closes = candleList.Select(c => (double)c.Close).ToList();

        if (closes.Count < slow + signal + 10)
            return;

        var key = Key(symbol, interval);
        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            var alphaFast = 2.0 / (fast + 1.0);
            var alphaSlow = 2.0 / (slow + 1.0);
            var alphaSignal = 2.0 / (signal + 1.0);

            // Seed fast EMA with SMA(fast), then warm it up to the slow boundary
            double fastEma = closes.Take(fast).Average();
            for (int i = fast; i < slow; i++)
                fastEma += alphaFast * (closes[i] - fastEma);

            // Seed slow EMA with SMA(slow)
            double slowEma = closes.Take(slow).Average();

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
                LiveMacd = null,
                LiveSignal = null,
                LiveHist = null,
                LastPrice = closes.Last(),
                LastTimestamp = candleList.Last().Timestamp
            };

            _periods[key] = (fast, slow, signal);
        }
    }


    // ----------------------------------------
    //  LIVE BAR UPDATE (preview-only, no state compounding)
    // ----------------------------------------
    /// <summary>
    /// Updates MACD preview state with a streaming bar close price.
    /// Computes preview from committed state but does NOT modify committed EMAs (prevents drift).
    /// This is the preferred method for MACD updates using streaming historical bars.
    /// </summary>
    public void UpdateOnBar(string symbol, string interval, decimal close, DateTime timestamp)
    {
        var key = Key(symbol, interval);
        
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

            // Save previous preview values BEFORE updating (bar-based)
            state.PreviousMacd = state.LiveMacd;
            state.PreviousSignal = state.LiveSignal;

            // TradingView-style intrabar preview: compute from committed state but DO NOT write back EMAs
            var previewFast = state.FastEma + alphaFast * (p - state.FastEma);
            var previewSlow = state.SlowEma + alphaSlow * (p - state.SlowEma);
            var previewMacd = previewFast - previewSlow;
            var previewSignal = state.Signal + alphaSignal * (previewMacd - state.Signal);

            state.LiveMacd = previewMacd;
            state.LiveSignal = previewSignal;
            state.LiveHist = previewMacd - previewSignal;
            
            state.LastPrice = p;
            state.LastTimestamp = timestamp;
        }
    }

    // ----------------------------------------
    //  LIVE TICK UPDATE (preview-only, no state compounding)
    // ----------------------------------------
    /// <summary>
    /// Updates MACD preview state with a tick price.
    /// Deprecated for MACD - use UpdateOnBar() instead for better accuracy.
    /// Kept for backward compatibility.
    /// </summary>
    [Obsolete("Use UpdateOnBar() instead for MACD updates. This method is kept for backward compatibility.")]
    public void UpdateOnTick(string symbol, string interval, decimal price, DateTime timestamp)
    {
        var key = Key(symbol, interval);
        
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

            // Save previous preview values BEFORE updating (tick-based)
            state.PreviousMacd = state.LiveMacd;
            state.PreviousSignal = state.LiveSignal;

            // TradingView-style intrabar preview: compute from committed state but DO NOT write back EMAs
            var previewFast = state.FastEma + alphaFast * (p - state.FastEma);
            var previewSlow = state.SlowEma + alphaSlow * (p - state.SlowEma);
            var previewMacd = previewFast - previewSlow;
            var previewSignal = state.Signal + alphaSignal * (previewMacd - state.Signal);

            state.LiveMacd = previewMacd;
            state.LiveSignal = previewSignal;
            state.LiveHist = previewMacd - previewSignal;
            
            state.LastPrice = p;
            state.LastTimestamp = timestamp;
        }
    }

    // ----------------------------------------
    //  FINALIZED CANDLE UPDATE (committed)
    // ----------------------------------------
    public void UpdateOnFinalizedCandle(string symbol, string interval, decimal close, DateTime ts)
    {
        var key = Key(symbol, interval);

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

            // Commit close into EMAs
            state.FastEma = state.FastEma + alphaFast * (p - state.FastEma);
            state.SlowEma = state.SlowEma + alphaSlow * (p - state.SlowEma);

            var macd = state.FastEma - state.SlowEma;
            state.Signal = state.Signal + alphaSignal * (macd - state.Signal);

            // Reset preview to committed immediately after close (optional but stabilizes display)
            state.LiveMacd = macd;
            state.LiveSignal = state.Signal;
            state.LiveHist = macd - state.Signal;

            state.LastPrice = p;
            state.LastTimestamp = ts;
        }
    }

    // ----------------------------------------
    //  GET CURRENT MACD VALUES
    // ----------------------------------------
    public (double macd, double signal, double hist)? GetLastMacd(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return null;

        // Prefer live preview if present
        if (s.LiveMacd.HasValue && s.LiveSignal.HasValue && s.LiveHist.HasValue)
            return (s.LiveMacd.Value, s.LiveSignal.Value, s.LiveHist.Value);

        double macd = s.FastEma - s.SlowEma;
        double hist = macd - s.Signal;

        return (macd, s.Signal, hist);
    }


    // ----------------------------------------
    //  GET PREVIOUS (needed for crossover detection)
    // ----------------------------------------
    public (double Macd, double Signal)? GetPreviousMacd(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return null;

        if (s.PreviousMacd == null || s.PreviousSignal == null)
            return null;

        return (s.PreviousMacd.Value, s.PreviousSignal.Value);
    }


    public bool TryGetState(string symbol, string interval, out MacdState state)
    {
        return _states.TryGetValue(Key(symbol, interval), out state!);
    }
}
