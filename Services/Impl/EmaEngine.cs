using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Generic EMA engine with TradingView-style behavior:
/// - Committed OnFinalizedCandle() updates (authoritative, matches TV long-run)
/// - UpdateOnBar() provides intrabar preview from streaming bars WITHOUT compounding state (prevents drift)
/// - Supports any EMA period (20, 50, 200, etc.) keyed by (Symbol, Interval, Period)
/// </summary>
public class EmaEngine
{
    private readonly ConcurrentDictionary<(string Symbol, string Interval, int Period), SingleEmaState> _states = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval, int Period), object> _locks = new();

    private (string Symbol, string Interval, int Period) Key(string symbol, string interval, int period) =>
        (symbol, interval, period);

    // ----------------------------------------
    //  HISTORICAL INITIALIZATION
    // ----------------------------------------
    /// <summary>
    /// Initializes EMA state from historical candlesticks for a specific period.
    /// </summary>
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

            // Seed EMA with SMA of first period values (TradingView approach)
            double ema = closes.Take(period).Average();

            // Continue EMA calculation from period onwards
            for (int i = period; i < closes.Count; i++)
            {
                ema = ema + alpha * (closes[i] - ema);
            }

            // Save state
            _states[key] = new SingleEmaState
            {
                Symbol = symbol,
                Interval = interval,
                Period = period,
                CommittedEma = ema,
                PreviewEma = null,
                PreviousEma = null,
                LastPrice = closes.Last(),
                LastTimestamp = candleList.Last().Timestamp
            };
        }
    }

    // ----------------------------------------
    //  LIVE BAR UPDATE (preview-only, no state compounding)
    // ----------------------------------------
    /// <summary>
    /// Updates EMA preview state with a streaming bar close price.
    /// Computes preview from committed state but does NOT modify committed EMA (prevents drift).
    /// This is the preferred method for EMA updates using streaming historical bars.
    /// </summary>
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

            // Save previous preview value BEFORE updating (bar-based)
            state.PreviousEma = state.PreviewEma;

            // TradingView-style intrabar preview: compute from committed state but DO NOT write back to committed EMA
            var previewEma = state.CommittedEma + alpha * (p - state.CommittedEma);

            state.PreviewEma = previewEma;
            state.LastPrice = p;
            state.LastTimestamp = timestamp;
        }
    }

    // ----------------------------------------
    //  FINALIZED CANDLE UPDATE (committed)
    // ----------------------------------------
    /// <summary>
    /// Commits EMA value when a candle closes.
    /// </summary>
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

            // Commit close into EMA
            state.CommittedEma = state.CommittedEma + alpha * (p - state.CommittedEma);

            // Reset preview to committed immediately after close (optional but stabilizes display)
            state.PreviewEma = state.CommittedEma;
            state.PreviousEma = null;

            state.LastPrice = p;
            state.LastTimestamp = ts;
        }
    }

    // ----------------------------------------
    //  GET CURRENT EMA VALUE
    // ----------------------------------------
    /// <summary>
    /// Gets the current EMA value (prefers preview, falls back to committed).
    /// </summary>
    public double? GetEma(string symbol, string interval, int period)
    {
        if (!_states.TryGetValue(Key(symbol, interval, period), out var state))
            return null;

        // Prefer live preview if present, otherwise use committed
        return state.PreviewEma ?? state.CommittedEma;
    }

    /// <summary>
    /// Checks if state exists for the given symbol, interval, and period.
    /// </summary>
    public bool TryGetState(string symbol, string interval, int period, out SingleEmaState state)
    {
        return _states.TryGetValue(Key(symbol, interval, period), out state!);
    }
}
