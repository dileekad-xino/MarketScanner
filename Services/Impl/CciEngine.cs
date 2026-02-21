using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// CCI engine with TradingView-style behavior:
/// - Committed UpdateOnFinalizedCandle() updates (authoritative, matches TV long-run)
/// - UpdateOnBar() provides intrabar preview from streaming bars WITHOUT compounding state (prevents drift)
/// - Uses Typical Price (H+L+C)/3 as source, matches TradingView hlc3
/// </summary>
public class CciEngine
{
    private readonly ConcurrentDictionary<(string Symbol, string Interval), CciState> _states = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval), object> _locks = new();
    private readonly ConcurrentDictionary<(string Symbol, string Interval), int> _periods = new();

    private (string Symbol, string Interval) Key(string symbol, string interval) =>
        (symbol, interval);

    /// <summary>
    /// Calculates Typical Price (H+L+C)/3 from a candlestick.
    /// </summary>
    private static double CalculateTypicalPrice(Candlestick candle)
    {
        return (double)((candle.High + candle.Low + candle.Close) / 3.0m);
    }

    /// <summary>
    /// Calculates Typical Price from OHLC values.
    /// </summary>
    private static double CalculateTypicalPrice(decimal high, decimal low, decimal close)
    {
        return (double)((high + low + close) / 3.0m);
    }

    /// <summary>
    /// Calculates Simple Moving Average of a list of values.
    /// </summary>
    private static double CalculateSma(List<double> values, int period)
    {
        if (values.Count < period) return 0.0;
        var sum = values.Skip(values.Count - period).Sum();
        return sum / period;
    }

    /// <summary>
    /// Calculates Mean Deviation for CCI.
    /// Mean Deviation = Average of |value - SMA| over the period
    /// This matches TradingView's ta.dev() function.
    /// </summary>
    private static double CalculateMeanDeviation(List<double> typicalPrices, int period, double sma)
    {
        if (typicalPrices.Count < period) return 0.0;
        var recentPrices = typicalPrices.Skip(typicalPrices.Count - period).ToList();
        var deviations = recentPrices.Select(tp => Math.Abs(tp - sma)).ToList();
        return deviations.Sum() / period;
    }

    // ----------------------------------------
    //  HISTORICAL INITIALIZATION
    // ----------------------------------------
    /// <summary>
    /// Initializes CCI state from historical candlesticks.
    /// Sets up committed state from candle closes (matches TradingView).
    /// </summary>
    public void Initialize(string symbol, string interval, IEnumerable<Candlestick> candles, int period = 14)
    {
        var candleList = candles.OrderBy(c => c.Timestamp).ToList();
        if (candleList.Count < period)
            return;

        var key = Key(symbol, interval);
        var lockObj = _locks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            // Calculate Typical Prices (H+L+C)/3 for all candles
            var typicalPrices = candleList.Select(CalculateTypicalPrice).ToList();

            // Calculate SMA of Typical Price
            var sma = CalculateSma(typicalPrices, period);

            // Calculate Mean Deviation
            var meanDeviation = CalculateMeanDeviation(typicalPrices, period, sma);

            // Calculate PreviousCommittedCci from second-to-last candle (if available)
            double? previousCommittedCci = null;
            if (typicalPrices.Count >= period + 1)
            {
                // Calculate CCI for second-to-last candle
                var previousTypicalPrices = typicalPrices.Take(typicalPrices.Count - 1).ToList();
                var previousSma = CalculateSma(previousTypicalPrices, period);
                var previousMeanDeviation = CalculateMeanDeviation(previousTypicalPrices, period, previousSma);
                if (previousMeanDeviation != 0)
                {
                    var previousLastTypicalPrice = previousTypicalPrices.Last();
                    previousCommittedCci = (previousLastTypicalPrice - previousSma) / (0.015 * previousMeanDeviation);
                }
            }

            // Initialize committed state
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
                EntryCciValue = null,
                ExitCciValue = null
            };

            _periods[key] = period;
        }
    }

    // ----------------------------------------
    //  LIVE BAR UPDATE (preview-only, no state compounding)
    // ----------------------------------------
    /// <summary>
    /// Updates CCI preview state with a streaming bar.
    /// Computes preview from committed state but does NOT modify committed averages (prevents drift).
    /// </summary>
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
            // Save previous preview value BEFORE updating (for crossover detection)
            state.PreviousCci = state.PreviewCci;

            // Calculate Typical Price for this bar
            double typicalPrice = CalculateTypicalPrice(high, low, close);

            // Create preview typical prices list (based on committed + current bar)
            var previewTypicalPrices = new List<double>(state.CommittedTypicalPrices);
            if (previewTypicalPrices.Count >= period)
            {
                previewTypicalPrices.RemoveAt(0); // Remove oldest
            }
            previewTypicalPrices.Add(typicalPrice);

            // Calculate preview SMA
            double previewSma = CalculateSma(previewTypicalPrices, period);

            // Calculate preview Mean Deviation
            double previewMeanDeviation = CalculateMeanDeviation(previewTypicalPrices, period, previewSma);

            // Store preview (doesn't affect committed state)
            state.PreviewSma = previewSma;
            state.PreviewMeanDeviation = previewMeanDeviation;
            state.PreviewLastTypicalPrice = typicalPrice;
            state.PreviewTypicalPrices = previewTypicalPrices;
        }
    }

    // ----------------------------------------
    //  FINALIZED CANDLE UPDATE (committed)
    // ----------------------------------------
    /// <summary>
    /// Commits candle data to CCI state.
    /// This is the authoritative update that matches TradingView's long-run values.
    /// </summary>
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
            // Save current committed CCI to PreviousCommittedCci before updating (CRITICAL for crossover detection)
            state.PreviousCommittedCci = state.CommittedCci;

            // Calculate Typical Price for this candle
            double typicalPrice = CalculateTypicalPrice(high, low, close);

            // Update committed typical prices list
            state.CommittedTypicalPrices.Add(typicalPrice);
            
            // Keep only the last (period * 2) values for efficiency (enough for calculations)
            if (state.CommittedTypicalPrices.Count > period * 2)
            {
                state.CommittedTypicalPrices.RemoveAt(0);
            }

            // Recalculate committed SMA
            state.CommittedSma = CalculateSma(state.CommittedTypicalPrices, period);

            // Recalculate committed Mean Deviation
            state.CommittedMeanDeviation = CalculateMeanDeviation(state.CommittedTypicalPrices, period, state.CommittedSma);

            // Update committed state
            state.CommittedLastTypicalPrice = typicalPrice;
            state.CommittedLastTimestamp = ts;

            // Reset preview to committed immediately after close (stabilizes display)
            state.PreviewSma = state.CommittedSma;
            state.PreviewMeanDeviation = state.CommittedMeanDeviation;
            state.PreviewLastTypicalPrice = typicalPrice;
            state.PreviewTypicalPrices = new List<double>(state.CommittedTypicalPrices);
        }
    }

    // ----------------------------------------
    //  GET CURRENT CCI VALUES
    // ----------------------------------------
    /// <summary>
    /// Gets the current CCI value for a symbol/interval.
    /// Returns preview CCI if available (for live updates), otherwise returns committed CCI.
    /// </summary>
    public double? GetCci(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return null;

        // Prefer live preview if present (for UI), otherwise return committed
        if (s.PreviewCci.HasValue)
            return s.PreviewCci.Value;

        return s.CommittedCci;
    }

    /// <summary>
    /// Gets both committed and preview CCI values.
    /// Useful for debugging or when you need to distinguish between the two.
    /// </summary>
    public (double CommittedCci, double? PreviewCci)? GetBothCci(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return null;

        return (s.CommittedCci, s.PreviewCci);
    }

    /// <summary>
    /// Gets the current CCI value along with the previous committed CCI value.
    /// Returns (current, previous) tuple where:
    /// - current: preview CCI if available (intrabar), otherwise committed CCI (after close)
    /// - previous: previous bar's committed CCI value (for crossover detection)
    /// </summary>
    public (double? current, double? previous) GetCciWithPrevious(string symbol, string interval)
    {
        if (!_states.TryGetValue(Key(symbol, interval), out var s))
            return (null, null);

        // Current: prefer preview if available (for live updates), otherwise committed
        double? current = s.PreviewCci ?? s.CommittedCci;

        // Previous: use PreviousCommittedCci (previous bar's committed value)
        double? previous = s.PreviousCommittedCci;

        return (current, previous);
    }

    /// <summary>
    /// Tries to get the CCI state for a symbol/interval.
    /// </summary>
    public bool TryGetState(string symbol, string interval, out CciState state)
    {
        return _states.TryGetValue(Key(symbol, interval), out state!);
    }
}

