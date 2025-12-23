using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// RSI engine with TradingView-style behavior:
/// - Committed UpdateOnFinalizedCandle() updates (authoritative, matches TV long-run)
/// - UpdateOnBar() provides intrabar preview from streaming bars WITHOUT compounding state (prevents drift)
/// - UpdateOnTick() is deprecated for RSI (kept for backward compatibility)
/// </summary>
public class RsiEngine
{
    private readonly ConcurrentDictionary<(string Symbol, string Interval), RsiState> _states = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval), object> _locks = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval), int> _periods = new();

    private (string Symbol, string Interval) Key(string symbol, string interval) =>
        (symbol, interval);

    // ----------------------------------------
    //  HISTORICAL INITIALIZATION
    // ----------------------------------------
    /// <summary>
    /// Initializes RSI state from historical candlesticks using Wilder's smoothing method.
    /// Sets up committed state from candle closes (matches TradingView).
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

        var key = Key(symbol, interval);
        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            // Apply Wilder's smoothing for remaining periods
            for (int i = period; i < changes.Length; i++)
            {
                avgGain = ((avgGain * (period - 1)) + gains[i]) / period;
                avgLoss = ((avgLoss * (period - 1)) + losses[i]) / period;
            }

            // Initialize committed state (from candle closes)
            _states[key] = new RsiState
            {
                Symbol = symbol,
                Interval = interval,
                CommittedAvgGain = avgGain,
                CommittedAvgLoss = avgLoss,
                CommittedLastClose = closes.Last(),
                CommittedLastTimestamp = candleList.Last().Timestamp,
                Period = period,
                PreviewAvgGain = null,
                PreviewAvgLoss = null,
                PreviewLastClose = null,
                PreviousRsi = null
            };

            _periods[key] = period;
        }
    }

    // ----------------------------------------
    //  LIVE BAR UPDATE (preview-only, no state compounding)
    // ----------------------------------------
    /// <summary>
    /// Updates RSI preview state with a streaming bar close price.
    /// Computes preview from committed state but does NOT modify committed averages (prevents drift).
    /// This is the preferred method for RSI updates using streaming historical bars.
    /// </summary>
    public void UpdateOnBar(string symbol, string interval, decimal close, DateTime timestamp)
    {
        var key = Key(symbol, interval);
        
        if (!_states.TryGetValue(key, out var state))
            return;
        
        if (!_periods.TryGetValue(key, out var period))
            return;
        
        var lockObj = _locks.GetOrAdd(key, _ => new object());
        
        lock (lockObj)
        {
            double p = (double)close;
            
            // Save previous preview value BEFORE updating (for crossover detection)
            state.PreviousRsi = state.PreviewRsi;

            // TradingView-style intrabar preview: compute from committed state but DO NOT write back
            double change = p - state.CommittedLastClose;
            double gain = change > 0 ? change : 0;
            double loss = change < 0 ? -change : 0;

            // Calculate preview averages from committed state (doesn't modify committed)
            double previewAvgGain = ((state.CommittedAvgGain * (period - 1)) + gain) / period;
            double previewAvgLoss = ((state.CommittedAvgLoss * (period - 1)) + loss) / period;

            // Store preview (doesn't affect committed state)
            state.PreviewAvgGain = previewAvgGain;
            state.PreviewAvgLoss = previewAvgLoss;
            state.PreviewLastClose = p;
        }
    }

    // ----------------------------------------
    //  LIVE TICK UPDATE (preview-only, no state compounding)
    // ----------------------------------------
    /// <summary>
    /// Updates RSI preview state with a live tick price.
    /// Deprecated for RSI - use UpdateOnBar() instead for better accuracy.
    /// Kept for backward compatibility.
    /// </summary>
    [Obsolete("Use UpdateOnBar() instead for RSI updates. This method is kept for backward compatibility.")]
    public void UpdateOnTick(string symbol, string interval, decimal price, DateTime timestamp)
    {
        var key = Key(symbol, interval);
        
        if (!_states.TryGetValue(key, out var state))
            return;
        
        if (!_periods.TryGetValue(key, out var period))
            return;
        
        var lockObj = _locks.GetOrAdd(key, _ => new object());
        
        lock (lockObj)
        {
            double p = (double)price;
            
            // Save previous preview value BEFORE updating (for crossover detection)
            state.PreviousRsi = state.PreviewRsi;

            // TradingView-style intrabar preview: compute from committed state but DO NOT write back
            double change = p - state.CommittedLastClose;
            double gain = change > 0 ? change : 0;
            double loss = change < 0 ? -change : 0;

            // Calculate preview averages from committed state (doesn't modify committed)
            double previewAvgGain = ((state.CommittedAvgGain * (period - 1)) + gain) / period;
            double previewAvgLoss = ((state.CommittedAvgLoss * (period - 1)) + loss) / period;

            // Store preview (doesn't affect committed state)
            state.PreviewAvgGain = previewAvgGain;
            state.PreviewAvgLoss = previewAvgLoss;
            state.PreviewLastClose = p;
        }
    }

    // ----------------------------------------
    //  FINALIZED CANDLE UPDATE (committed)
    // ----------------------------------------
    /// <summary>
    /// Commits candle close price to RSI state using Wilder's smoothing.
    /// This is the authoritative update that matches TradingView's long-run values.
    /// </summary>
    public void UpdateOnFinalizedCandle(string symbol, string interval, decimal close, DateTime ts)
    {
        var key = Key(symbol, interval);

        if (!_states.TryGetValue(key, out var state))
            return;

        if (!_periods.TryGetValue(key, out var period))
            return;

        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            double p = (double)close;

            // Commit close into Wilder's smoothing (authoritative state)
            double change = p - state.CommittedLastClose;
            double gain = change > 0 ? change : 0;
            double loss = change < 0 ? -change : 0;

            // Update committed averages (matches TradingView)
            state.CommittedAvgGain = ((state.CommittedAvgGain * (period - 1)) + gain) / period;
            state.CommittedAvgLoss = ((state.CommittedAvgLoss * (period - 1)) + loss) / period;
            state.CommittedLastClose = p;
            state.CommittedLastTimestamp = ts;

            // Reset preview to committed immediately after close (stabilizes display)
            state.PreviewAvgGain = state.CommittedAvgGain;
            state.PreviewAvgLoss = state.CommittedAvgLoss;
            state.PreviewLastClose = p;
        }
    }

    // ----------------------------------------
    //  GET CURRENT RSI VALUES
    // ----------------------------------------
    /// <summary>
    /// Gets the current RSI value for a symbol/interval.
    /// Returns preview RSI if available (for live updates), otherwise returns committed RSI.
    /// </summary>
    public double? GetRsi(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return null;

        // Prefer live preview if present (for UI), otherwise return committed
        if (s.PreviewRsi.HasValue)
            return s.PreviewRsi.Value;

        return s.CommittedRsi;
    }

    /// <summary>
    /// Gets both committed and preview RSI values.
    /// Useful for debugging or when you need to distinguish between the two.
    /// </summary>
    public (double CommittedRsi, double? PreviewRsi)? GetBothRsi(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return null;

        return (s.CommittedRsi, s.PreviewRsi);
    }

    /// <summary>
    /// Tries to get the RSI state for a symbol/interval.
    /// </summary>
    public bool TryGetState(string symbol, string interval, out RsiState state)
    {
        return _states.TryGetValue(Key(symbol, interval), out state!);
    }

    // ----------------------------------------
    //  BACKWARD COMPATIBILITY
    // ----------------------------------------
    /// <summary>
    /// Backward compatibility method. Calls UpdateOnTick internally.
    /// </summary>
    [Obsolete("Use UpdateOnTick instead for dual-state behavior")]
    public void UpdateLive(string symbol, string interval, decimal tickPrice, DateTime timestamp)
    {
        UpdateOnTick(symbol, interval, tickPrice, timestamp);
    }
}

