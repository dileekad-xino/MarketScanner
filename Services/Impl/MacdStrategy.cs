using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Trading strategy based on MACD (Moving Average Convergence Divergence) indicator.
/// Uses real-time candlesticks to calculate MACD and generate buy/sell signals.
/// Uses true incremental EMA updates for efficiency and accuracy.
/// </summary>
public class MacdStrategy : IAlgoStrategy
{
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ILogger<MacdStrategy> _logger;
    
    // Cache for EMA state per symbol+interval to enable incremental updates
    private readonly ConcurrentDictionary<string, EmaState> _emaStateCache = new();
    
    // Per-symbol locks for thread-safe incremental updates
    private readonly ConcurrentDictionary<string, object> _updateLocks = new();
    
    // Gap detection multiplier (default: 5x interval)
    private const int MaxGapMultiplier = 5;

    public string Name => "MACD Strategy";
    public string Description => "Uses MACD crossover and histogram signals from real-time candlesticks for trading decisions.";

    public MacdStrategy(
        ICandlestickStorage candlestickStorage,
        CandlestickConfig config,
        ILogger<MacdStrategy> logger)
    {
        _candlestickStorage = candlestickStorage;
        _config = config;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            var interval = GetIntervalString(_config.IntervalSeconds);
            var cacheKey = $"{symbol.Symbol}_{interval}";
            
            // Get per-symbol lock for thread-safe updates
            var lockObj = _updateLocks.GetOrAdd(cacheKey, _ => new object());

            // Get recent candlesticks
            var minRequired = _config.Macd.SlowPeriod + _config.Macd.SignalPeriod;
            
            // We now always return full candle history from storage.
            // MACD only needs minRequired, so we trim manually below.
            var calculationWindow = int.MaxValue;
            
            var allCandlesticks = _candlestickStorage.GetCandlesticks(symbol.Symbol, interval, calculationWindow)
                .OrderBy(c => c.Timestamp)
                .ToList();
            
            // Get the latest candlestick from storage directly (most recently added)
            // This ensures we always include the newest finalized candle, even if its timestamp
            // appears older than historical bars due to timezone mismatches
            var latestCandlestick = _candlestickStorage.GetLatestCandlestick(symbol.Symbol, interval);
            
            // Diagnostic logging before trim
            _logger.LogInformation("MACD Debug: Before trim - Total={Total}, MinRequired={Min}, LatestFromStorage={Latest:o}", 
                allCandlesticks.Count, minRequired, latestCandlestick?.Timestamp);
            
            if (allCandlesticks.Any())
            {
                _logger.LogInformation("MACD Debug: Before trim - First={First:o}, Last={Last:o}",
                    allCandlesticks.First().Timestamp,
                    allCandlesticks.Last().Timestamp);
            }
            
            // Trim only if we have way too many (keep more buffer to ensure newest candles are included)
            // Use minRequired + 50 to ensure we have plenty of buffer for the newest candles
            if (allCandlesticks.Count > minRequired + 50)
            {
                var beforeTrim = allCandlesticks.Count;
                var newestTimestamp = allCandlesticks.Last().Timestamp;
                
                // Keep the last (minRequired + 50) candles to ensure we always have the newest ones
                allCandlesticks = allCandlesticks
                    .Skip(allCandlesticks.Count - (minRequired + 50))
                    .ToList();
                
                // CRITICAL FIX: If the latest candlestick from storage is not in the trimmed list, add it
                // This handles the case where the newest candle has an older timestamp than historical bars
                // (e.g., due to timezone conversion differences between live ticks and historical data)
                if (latestCandlestick != null)
                {
                    var normalizedLatest = TimestampUtils.NormalizeAndTruncateToInterval(latestCandlestick.Timestamp, _config.IntervalSeconds);
                    var isInList = allCandlesticks.Any(c => 
                    {
                        var normalizedC = TimestampUtils.NormalizeAndTruncateToInterval(c.Timestamp, _config.IntervalSeconds);
                        return normalizedC.Ticks == normalizedLatest.Ticks;
                    });
                    
                    if (!isInList)
                    {
                        _logger.LogWarning("MACD: Latest candlestick {LatestTs:o} not in trimmed list (trimmed range: {First:o} to {Last:o}), adding it back",
                            latestCandlestick.Timestamp,
                            allCandlesticks.FirstOrDefault()?.Timestamp,
                            allCandlesticks.LastOrDefault()?.Timestamp);
                        allCandlesticks.Add(latestCandlestick);
                        allCandlesticks = allCandlesticks.OrderBy(c => c.Timestamp).ToList();
                    }
                }
                
                // Verify the newest candle is still included
                if (!allCandlesticks.Any() || allCandlesticks.Last().Timestamp != newestTimestamp)
                {
                    _logger.LogWarning("MACD: Trimming removed newest candle! Before={Before}, After={After}, Newest={Newest:o}",
                        beforeTrim, allCandlesticks.Count, newestTimestamp);
                }
                
                _logger.LogInformation("MACD Debug: After trim - Count={Count}, First={First:o}, Last={Last:o}",
                    allCandlesticks.Count, 
                    allCandlesticks.First().Timestamp,
                    allCandlesticks.Last().Timestamp);
            }
            
            // MACD diagnostic logging
            if (allCandlesticks.Any())
            {
                _logger.LogInformation(
                    "MACD Debug: Loaded {Count} candles, Latest={LatestTs}",
                    allCandlesticks.Count,
                    allCandlesticks.Last().Timestamp);
            }

            _logger.LogInformation("MacdStrategy: Got {Count} candlesticks for {Symbol} (need {Min})",
                allCandlesticks.Count, symbol.Symbol, minRequired);

            if (allCandlesticks.Count < minRequired)
            {
                _logger.LogWarning("MacdStrategy: Insufficient candlesticks for {Symbol}: need {Min}, got {Count}",
                    symbol.Symbol, minRequired, allCandlesticks.Count);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Insufficient candlesticks for MACD: need {minRequired}, got {allCandlesticks.Count}",
                    Timestamp: DateTime.UtcNow
                );
            }

            // Deduplicate and order candlesticks (timestamps should already be UTC and truncated from storage)
            var uniqueCandlesticks = DeduplicateCandlesticks(allCandlesticks);
            
            // Validate all timestamps are UTC and truncated (should already be from storage, but verify for safety)
            uniqueCandlesticks = uniqueCandlesticks
                .Select(c => 
                {
                    // Safety check: ensure UTC and truncated (should already be from CandlestickStorage)
                    var truncated = TimestampUtils.NormalizeAndTruncateToInterval(c.Timestamp, _config.IntervalSeconds);
                    if (c.Timestamp != truncated)
                    {
                        _logger.LogWarning("MacdStrategy: Non-truncated timestamp detected for {Symbol} at {Time}, truncating to {Truncated}",
                            symbol.Symbol, c.Timestamp, truncated);
                        return c with { Timestamp = truncated };
                    }
                    return c;
                })
                .OrderBy(c => c.Timestamp)
                .ToList();
            
            // Try to load EMA state from cache or persistence
            var emaState = await LoadEmaStateAsync(cacheKey, symbol.Symbol, interval, ct);
            
            // Normalize and truncate LastTimestamp if state exists
            if (emaState != null)
            {
                emaState.LastTimestamp = TimestampUtils.NormalizeAndTruncateToInterval(emaState.LastTimestamp, _config.IntervalSeconds);
            }
            
            _logger.LogInformation("MacdStrategy: EMA state check for {Symbol} - Found={Found}, LastTimestamp={LastTimestamp} (Kind={Kind})", 
                symbol.Symbol, emaState != null, emaState?.LastTimestamp, emaState?.LastTimestamp.Kind);
            
            // Determine if we should use incremental update or force recalculation
            var shouldForceRecalc = ShouldForceRecalculation(emaState, uniqueCandlesticks, symbol.Symbol, interval);
            
            _logger.LogInformation("MacdStrategy: Should force recalc for {Symbol} = {ForceRecalc}", symbol.Symbol, shouldForceRecalc);
            
            MacdData? macd;
            decimal? previousMacdLine = null;
            decimal? previousSignalLine = null;

            if (emaState != null && !shouldForceRecalc)
            {
                // Incremental update path: process all new candlesticks
                var lastCandle = uniqueCandlesticks.LastOrDefault();
                _logger.LogInformation("MacdStrategy: Checking incremental update for {Symbol} - LastCandleTime={LastCandleTime}, LastProcessedTime={LastProcessedTime}, TotalCandles={Total}", 
                    symbol.Symbol, lastCandle?.Timestamp, emaState.LastTimestamp, uniqueCandlesticks.Count);
                
                var newCandlesticks = GetIncrementalCandles(emaState.LastTimestamp, uniqueCandlesticks);
                
                _logger.LogInformation("MacdStrategy: Found {Count} new candlesticks for {Symbol} (out of {Total} total)", 
                    newCandlesticks.Count, symbol.Symbol, uniqueCandlesticks.Count);
                
                // Diagnostic logging when no new candlesticks found
                if (newCandlesticks.Count == 0 && uniqueCandlesticks.Any())
                {
                    var latestCandle = uniqueCandlesticks.Last();
                    var truncatedLatest = TimestampUtils.NormalizeAndTruncateToInterval(latestCandle.Timestamp, _config.IntervalSeconds);
                    var truncatedLast = TimestampUtils.NormalizeAndTruncateToInterval(emaState.LastTimestamp, _config.IntervalSeconds);
                    var differenceTicks = truncatedLatest.Ticks - truncatedLast.Ticks;
                    
                    _logger.LogWarning(
                        "MacdStrategy: TimestampMismatch - lastProcessed={Last:o} (trunc={LastTrunc:o}), latest={Latest:o} (trunc={LatestTrunc:o}), differenceTicks={DiffTicks}",
                        emaState.LastTimestamp, truncatedLast,
                        latestCandle.Timestamp, truncatedLatest,
                        differenceTicks);
                }
                
                // Same-timestamp fallback: check if close price changed
                if (newCandlesticks.Count == 0)
                {
                    var truncatedLast = TimestampUtils.NormalizeAndTruncateToInterval(emaState.LastTimestamp, _config.IntervalSeconds);
                    // Compare using truncated timestamps for exact match
                    var sameTs = uniqueCandlesticks.LastOrDefault(c => 
                    {
                        var truncatedCandle = TimestampUtils.NormalizeAndTruncateToInterval(c.Timestamp, _config.IntervalSeconds);
                        return truncatedCandle.Ticks == truncatedLast.Ticks;
                    });
                    const decimal SAME_CLOSE_EPS = 0.0000001m;

                    _logger.LogInformation(
                        "MacdStrategy: Same-timestamp check - sameTs={Found}, LastProcessedClose={LastClose}, LatestClose={LatestClose}",
                        sameTs != null ? "FOUND" : "NOT FOUND",
                        emaState.LastProcessedClose,
                        sameTs?.Close);

                    if (sameTs != null &&
                        (!emaState.LastProcessedClose.HasValue ||
                         Math.Abs(sameTs.Close - emaState.LastProcessedClose.Value) > SAME_CLOSE_EPS))
                    {
                        _logger.LogInformation(
                            "MacdStrategy: Same-timestamp candle close changed. Prev={Prev}, New={New} — reprocessing.",
                            emaState.LastProcessedClose, sameTs.Close);

                        newCandlesticks = new List<Candlestick> { sameTs };
                    }
                    else if (sameTs != null)
                    {
                        _logger.LogInformation(
                            "MacdStrategy: Same-timestamp candle found but close unchanged (Prev={Prev}, New={New}, Diff={Diff}) — skipping.",
                            emaState.LastProcessedClose, sameTs.Close,
                            emaState.LastProcessedClose.HasValue ? Math.Abs(sameTs.Close - emaState.LastProcessedClose.Value) : 0);
                    }
                }
                
                if (newCandlesticks.Count > 0)
                {
                    // All incremental updates must be thread-safe
                    lock (lockObj)
                    {
                        _logger.LogInformation("MacdStrategy: Processing {Count} new candlesticks incrementally for {Symbol} (last processed: {LastTimestamp})",
                            newCandlesticks.Count, symbol.Symbol, emaState.LastTimestamp);
                        
                        // Store previous values before updating
                        previousMacdLine = emaState.MacdLine;
                        previousSignalLine = emaState.SignalEma;
                        
                        // Perform incremental update for all new candlesticks
                        macd = PerformIncrementalUpdate(emaState, newCandlesticks);
                        
                        // Save state after update (inside lock for thread safety)
                        // Note: SaveEmaStateAsync is async but we're in a lock - this is acceptable
                        // as the persistence is optional and lightweight
                    }
                    
                    // Persist state outside lock to avoid blocking
                    await SaveEmaStateAsync(emaState, ct);
                }
                else
                {
                    // No new candlesticks, return current state
                    _logger.LogInformation("MacdStrategy: No new candlesticks for {Symbol}, using cached MACD values. LastProcessed={LastProcessed}, LatestCandle={LatestCandle}", 
                        symbol.Symbol, emaState.LastTimestamp, lastCandle?.Timestamp);
                    macd = new MacdData(
                        Symbol: symbol.Symbol,
                        MacdLine: emaState.MacdLine,
                        SignalLine: emaState.SignalEma,
                        Histogram: emaState.MacdLine - emaState.SignalEma,
                        Timestamp: DateTime.UtcNow,
                        Interval: interval
                    );
                    previousMacdLine = emaState.PreviousMacdLine;
                    previousSignalLine = emaState.PreviousSignalLine;
                }
            }
            else
            {
                // Initial calculation: use most recent N candles (no date filtering)
                var candlesticksForInitial = uniqueCandlesticks
                    .TakeLast(Math.Min(calculationWindow, uniqueCandlesticks.Count))
                    .ToList();

                if (candlesticksForInitial.Count < minRequired)
                {
                    _logger.LogWarning("MacdStrategy: Insufficient candlesticks for initial calculation for {Symbol}: need {Min}, got {Count}",
                        symbol.Symbol, minRequired, candlesticksForInitial.Count);
                    return new AlgoResult(
                        Symbol: symbol.Symbol,
                        Action: AlgoAction.Hold,
                        Price: symbol.LastPrice,
                        Reason: $"Insufficient candlesticks for initial MACD: need {minRequired}, got {candlesticksForInitial.Count}",
                        Timestamp: DateTime.UtcNow
                    );
                }

                _logger.LogInformation("MacdStrategy: Calculating initial EMAs from {Count} candlesticks for {Symbol} (Fast={FastPeriod}, Slow={SlowPeriod}, Signal={SignalPeriod})",
                    candlesticksForInitial.Count, symbol.Symbol, _config.Macd.FastPeriod, _config.Macd.SlowPeriod, _config.Macd.SignalPeriod);

                var closes = candlesticksForInitial.Select(c => c.Close).ToList();
                var lastCandlestick = candlesticksForInitial.Last();

                // Perform initial calculation
                var initialState = PerformInitialCalculation(closes, symbol.Symbol, interval, lastCandlestick.Timestamp, lastCandlestick.Close);
                
                if (initialState == null)
                {
                    _logger.LogWarning("MacdStrategy: Initial EMA state calculation returned null for {Symbol}", symbol.Symbol);
                    return new AlgoResult(
                        Symbol: symbol.Symbol,
                        Action: AlgoAction.Hold,
                        Price: symbol.LastPrice,
                        Reason: "MACD calculation failed",
                        Timestamp: DateTime.UtcNow
                    );
                }

                // Store state in cache
                _emaStateCache.AddOrUpdate(cacheKey, initialState, (key, old) => initialState);
                
                // Save state
                await SaveEmaStateAsync(initialState, ct);

                // Get previous MACD for crossover detection
                if (closes.Count > 1)
                {
                    var prevCloses = closes.Take(closes.Count - 1).ToList();
                    var prevState = TechnicalIndicators.CalculateInitialEmaState(
                        prevCloses,
                        _config.Macd.FastPeriod,
                        _config.Macd.SlowPeriod,
                        _config.Macd.SignalPeriod);
                    
                    if (prevState != null)
                    {
                        previousMacdLine = prevState.MacdLine;
                        previousSignalLine = prevState.SignalEma;
                        initialState.PreviousMacdLine = prevState.MacdLine;
                        initialState.PreviousSignalLine = prevState.SignalEma;
                    }
                }

                macd = new MacdData(
                    Symbol: symbol.Symbol,
                    MacdLine: initialState.MacdLine,
                    SignalLine: initialState.SignalEma,
                    Histogram: initialState.MacdLine - initialState.SignalEma,
                    Timestamp: DateTime.UtcNow,
                    Interval: interval
                );
            }

            // Log final MACD values
            _logger.LogInformation("MacdStrategy: Final MACD values for {Symbol} - MACD={Macd:F4}, Signal={Signal:F4}, Histogram={Histogram:F4}, Last Price={Price:F4}",
                symbol.Symbol, macd.MacdLine, macd.SignalLine, macd.Histogram, symbol.LastPrice);

            // Detect crossovers and generate signals
            var (action, reason, crossover) = DetectSignals(macd, previousMacdLine, previousSignalLine);

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: action,
                Price: symbol.LastPrice,
                Reason: reason,
                Timestamp: DateTime.UtcNow,
                Macd: macd,
                Crossover: crossover
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MACD strategy failed for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"MACD strategy error: {ex.Message}",
                Timestamp: DateTime.UtcNow
            );
        }
    }

    /// <summary>
    /// Deduplicates candlesticks by timestamp, keeping the one with highest volume.
    /// Normalizes and truncates timestamps to interval boundaries before grouping to ensure reliable comparison.
    /// </summary>
    private List<Candlestick> DeduplicateCandlesticks(IReadOnlyList<Candlestick> candlesticks)
    {
        // Normalize and truncate all timestamps to interval boundaries before grouping
        var normalized = candlesticks
            .Select(c => c with { Timestamp = TimestampUtils.NormalizeAndTruncateToInterval(c.Timestamp, _config.IntervalSeconds) })
            .OrderBy(c => c.Timestamp)
            .ToList();
        
        // Group by truncated timestamp and keep highest volume
        var unique = normalized
            .GroupBy(c => c.Timestamp)
            .Select(g => g.OrderByDescending(c => c.Volume).First())
            .OrderBy(c => c.Timestamp)
            .ToList();

        if (normalized.Count != unique.Count)
        {
            _logger.LogInformation("MacdStrategy: Deduplicated {OriginalCount} candlesticks to {UniqueCount} (removed {DuplicateCount} duplicates)",
                normalized.Count, unique.Count, normalized.Count - unique.Count);
        }

        return unique;
    }

    /// <summary>
    /// Gets all candlesticks that are strictly newer than the last processed timestamp.
    /// Important: Do NOT automatically include same-timestamp candles.
    /// All timestamps are normalized and truncated to interval boundaries for comparison.
    /// </summary>
    private List<Candlestick> GetIncrementalCandles(DateTime lastTimestamp, List<Candlestick> allCandles)
    {
        // Normalize and truncate lastTimestamp to interval boundary
        var truncatedLast = TimestampUtils.NormalizeAndTruncateToInterval(lastTimestamp, _config.IntervalSeconds);
        
        // Filter candlesticks with truncated timestamps strictly greater than truncated lastTimestamp
        var result = allCandles
            .Where(c => 
            {
                var truncatedCandle = TimestampUtils.NormalizeAndTruncateToInterval(c.Timestamp, _config.IntervalSeconds);
                return truncatedCandle > truncatedLast;
            })
            .OrderBy(c => c.Timestamp)
            .ToList();
        
        // Diagnostic logging
        if (result.Count > 0)
        {
            var firstTruncated = TimestampUtils.NormalizeAndTruncateToInterval(result.First().Timestamp, _config.IntervalSeconds);
            _logger.LogDebug(
                "GetIncrementalCandles: TS TRACE - lastRaw={LastRaw:o}, lastTrunc={LastTrunc:o}, firstRaw={FirstRaw:o}, firstTrunc={FirstTrunc:o}, count={Count}",
                lastTimestamp, truncatedLast,
                result.First().Timestamp, firstTruncated,
                result.Count);
        }
        
        return result;
    }
    
    /// <summary>
    /// Normalizes a DateTime to UTC, handling all DateTimeKind values.
    /// </summary>

    /// <summary>
    /// Determines if we should force a full recalculation.
    /// </summary>
    private bool ShouldForceRecalculation(EmaState? emaState, List<Candlestick> candlesticks, string symbol, string interval)
    {
        if (emaState == null)
        {
            _logger.LogInformation("MacdStrategy: No EMA state found for {Symbol}, will perform initial calculation", symbol);
            return true;
        }

        // Check if state matches current configuration
        if (emaState.Symbol != symbol ||
            emaState.Interval != interval ||
            emaState.FastPeriod != _config.Macd.FastPeriod ||
            emaState.SlowPeriod != _config.Macd.SlowPeriod ||
            emaState.SignalPeriod != _config.Macd.SignalPeriod)
        {
            _logger.LogInformation("MacdStrategy: EMA state configuration mismatch for {Symbol}, will recalculate", symbol);
            return true;
        }

        if (!emaState.IsInitialized)
        {
            _logger.LogInformation("MacdStrategy: EMA state not fully initialized for {Symbol}, will recalculate", symbol);
            return true;
        }

        if (!candlesticks.Any())
        {
            return false; // No candlesticks, can't recalculate anyway
        }

        var lastCandle = candlesticks.Last();
        var newCandles = GetIncrementalCandles(emaState.LastTimestamp, candlesticks);

        // If there are no new candles, no need to recalculate
        if (newCandles.Count == 0)
        {
            return false;
        }

        // Check for data gaps - normalize and truncate timestamps for comparison
        var truncatedLastCandleTs = TimestampUtils.NormalizeAndTruncateToInterval(lastCandle.Timestamp, _config.IntervalSeconds);
        var truncatedLastProcessedTs = TimestampUtils.NormalizeAndTruncateToInterval(emaState.LastTimestamp, _config.IntervalSeconds);
        var timeDiff = truncatedLastCandleTs - truncatedLastProcessedTs;
        var expectedInterval = TimeSpan.FromSeconds(_config.IntervalSeconds);
        var maxGap = expectedInterval * MaxGapMultiplier;

        // Diagnostic logging
        _logger.LogDebug(
            "ShouldForceRecalculation: TS TRACE - lastCandleRaw={LastCandleRaw:o}, lastCandleTrunc={LastCandleTrunc:o}, lastProcessedRaw={LastProcessedRaw:o}, lastProcessedTrunc={LastProcessedTrunc:o}, timeDiff={TimeDiff}",
            lastCandle.Timestamp, truncatedLastCandleTs,
            emaState.LastTimestamp, truncatedLastProcessedTs,
            timeDiff);

        // If gap is too large AND there are no intermediate candles, force recalculation
        if (timeDiff > maxGap)
        {
            // Check if we have intermediate candles (candles between lastTimestamp and lastCandle)
            // Use truncated timestamps for comparison
            var hasIntermediateCandles = candlesticks.Any(c => 
            {
                var truncatedCTs = TimestampUtils.NormalizeAndTruncateToInterval(c.Timestamp, _config.IntervalSeconds);
                return truncatedCTs > truncatedLastProcessedTs && 
                       truncatedCTs < truncatedLastCandleTs;
            });

            if (!hasIntermediateCandles)
            {
                _logger.LogWarning("MacdStrategy: Large data gap detected for {Symbol} ({Gap} > {MaxGap}) with no intermediate candles, forcing recalculation",
                    symbol, timeDiff, maxGap);
                return true;
            }
            else
            {
                _logger.LogInformation("MacdStrategy: Large gap detected for {Symbol} ({Gap} > {MaxGap}) but intermediate candles exist, using incremental update",
                    symbol, timeDiff, maxGap);
            }
        }

        return false;
    }

    /// <summary>
    /// Performs incremental EMA update for all new candlesticks sequentially.
    /// </summary>
    private MacdData PerformIncrementalUpdate(EmaState emaState, List<Candlestick> newCandlesticks)
    {
        _logger.LogInformation("MacdStrategy: Performing incremental update for {Count} candlesticks. Starting state - Fast EMA={FastEma:F4}, Slow EMA={SlowEma:F4}, MACD={Macd:F4}, Signal={Signal:F4}",
            newCandlesticks.Count, emaState.FastEma, emaState.SlowEma, emaState.MacdLine, emaState.SignalEma);

        // Calculate alpha values (multipliers) for each EMA
        var alphaFast = 2.0m / (_config.Macd.FastPeriod + 1);
        var alphaSlow = 2.0m / (_config.Macd.SlowPeriod + 1);
        var alphaSignal = 2.0m / (_config.Macd.SignalPeriod + 1);

        // Process each new candlestick sequentially
        foreach (var candle in newCandlesticks)
        {
            // Store previous values for crossover detection (before first update)
            if (candle == newCandlesticks.First())
            {
                emaState.PreviousMacdLine = emaState.MacdLine;
                emaState.PreviousSignalLine = emaState.SignalEma;
            }

            // Update Fast EMA: EMA = alpha * price + (1 - alpha) * previousEMA
            emaState.FastEma = alphaFast * candle.Close + (1 - alphaFast) * emaState.FastEma;

            // Update Slow EMA
            emaState.SlowEma = alphaSlow * candle.Close + (1 - alphaSlow) * emaState.SlowEma;

            // Calculate MACD line
            emaState.MacdLine = emaState.FastEma - emaState.SlowEma;

            // Update Signal EMA (EMA of MACD line)
            emaState.SignalEma = alphaSignal * emaState.MacdLine + (1 - alphaSignal) * emaState.SignalEma;

            // Update timestamp, count, and last processed close - ensure timestamp is normalized and truncated
            emaState.LastTimestamp = TimestampUtils.NormalizeAndTruncateToInterval(candle.Timestamp, _config.IntervalSeconds);
            emaState.LastProcessedClose = candle.Close;
            emaState.ProcessedCount++;
            
            // Debug log for tracing incremental updates
            _logger.LogDebug(
                "MACD-IncUpdate | {Symbol} | Time={Time:o} | Close={Close:F6} | Fast={Fast:F6} | Slow={Slow:F6} | MACD={Macd:F6} | Signal={Signal:F6} | PrevMACD={PrevMacd:F6} | PrevSignal={PrevSignal:F6}",
                emaState.Symbol,
                candle.Timestamp,
                candle.Close,
                emaState.FastEma,
                emaState.SlowEma,
                emaState.MacdLine,
                emaState.SignalEma,
                emaState.PreviousMacdLine,
                emaState.PreviousSignalLine);
        }

        _logger.LogInformation("MacdStrategy: Incremental update completed. Final state - Fast EMA={FastEma:F4}, Slow EMA={SlowEma:F4}, MACD={Macd:F4}, Signal={Signal:F4}, Histogram={Histogram:F4}",
            emaState.FastEma, emaState.SlowEma, emaState.MacdLine, emaState.SignalEma, emaState.MacdLine - emaState.SignalEma);

        return new MacdData(
            Symbol: emaState.Symbol,
            MacdLine: emaState.MacdLine,
            SignalLine: emaState.SignalEma,
            Histogram: emaState.MacdLine - emaState.SignalEma,
            Timestamp: DateTime.UtcNow,
            Interval: emaState.Interval
        );
    }

    /// <summary>
    /// Performs initial EMA calculation from a list of closing prices.
    /// </summary>
    private EmaState? PerformInitialCalculation(
        IReadOnlyList<decimal> closes,
        string symbol,
        string interval,
        DateTime lastTimestamp,
        decimal lastClose)
    {
        var initialState = TechnicalIndicators.CalculateInitialEmaState(
            closes,
            _config.Macd.FastPeriod,
            _config.Macd.SlowPeriod,
            _config.Macd.SignalPeriod);

        if (initialState == null)
        {
            return null;
        }

        // Set metadata - normalize and truncate LastTimestamp to interval boundary
        initialState.Symbol = symbol;
        initialState.Interval = interval;
        initialState.LastTimestamp = TimestampUtils.NormalizeAndTruncateToInterval(lastTimestamp, _config.IntervalSeconds);
        initialState.LastProcessedClose = lastClose;

        // Diagnostic logging
        _logger.LogDebug(
            "PerformInitialCalculation: TS TRACE - raw={Raw:o}, utc={Utc:o}, truncated={Trunc:o}",
            lastTimestamp,
            TimestampUtils.NormalizeToUtc(lastTimestamp),
            initialState.LastTimestamp);
        initialState.FastPeriod = _config.Macd.FastPeriod;
        initialState.SlowPeriod = _config.Macd.SlowPeriod;
        initialState.SignalPeriod = _config.Macd.SignalPeriod;

        _logger.LogInformation("MacdStrategy: Initial calculation completed for {Symbol} - Fast EMA={FastEma:F4}, Slow EMA={SlowEma:F4}, MACD={Macd:F4}, Signal={Signal:F4}, Histogram={Histogram:F4}",
            symbol, initialState.FastEma, initialState.SlowEma, initialState.MacdLine, initialState.SignalEma, initialState.MacdLine - initialState.SignalEma);

        return initialState;
    }

    /// <summary>
    /// Loads EMA state from cache or persistence.
    /// </summary>
    private async Task<EmaState?> LoadEmaStateAsync(string cacheKey, string symbol, string interval, CancellationToken ct)
    {
        // Try to get from cache first
        if (_emaStateCache.TryGetValue(cacheKey, out var cachedState))
        {
            _logger.LogDebug("MacdStrategy: Loaded EMA state from cache for {Symbol}", symbol);
            return cachedState;
        }

        // TODO: Load from persistence (database/file) if implemented
        // var persistedState = await _persistenceService.LoadEmaStateAsync(symbol, interval, ct);
        // if (persistedState != null)
        // {
        //     _emaStateCache[cacheKey] = persistedState;
        //     return persistedState;
        // }

        return null;
    }

    /// <summary>
    /// Saves EMA state to persistence (optional).
    /// </summary>
    private async Task SaveEmaStateAsync(EmaState state, CancellationToken ct)
    {
        // TODO: Save to persistence (database/file) if implemented
        // await _persistenceService.SaveEmaStateAsync(state, ct);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Detects MACD crossovers and generates trading signals.
    /// Uses epsilon-based comparisons to prevent false crossovers from micro-jitter.
    /// </summary>
    private (AlgoAction Action, string Reason, CrossoverStatus Crossover) DetectSignals(
        MacdData macd,
        decimal? previousMacdLine,
        decimal? previousSignalLine)
    {
        var action = AlgoAction.Hold;
        var reason = "";
        var crossover = CrossoverStatus.None;

        // Epsilon for crossover detection to prevent false positives from floating-point jitter
        const decimal CROSS_EPS = 0.0000001m;

        // Epsilon-safe comparisons for MACD vs Signal
        bool macdAbove = (macd.MacdLine - macd.SignalLine) > CROSS_EPS;
        bool macdBelow = (macd.SignalLine - macd.MacdLine) > CROSS_EPS;

        // Detect crossovers using previous MACD values with epsilon tolerance
        bool bullishCrossover = previousMacdLine.HasValue &&
                                previousSignalLine.HasValue &&
                                macdAbove &&
                                (previousMacdLine.Value <= previousSignalLine.Value + CROSS_EPS);

        bool bearishCrossover = previousMacdLine.HasValue &&
                                previousSignalLine.HasValue &&
                                macdBelow &&
                                (previousMacdLine.Value >= previousSignalLine.Value - CROSS_EPS);

        // BUY: Only when both lines are positive AND MACD crosses above Signal
        if (bullishCrossover && macd.MacdLine > 0 && macd.SignalLine > 0)
        {
            action = AlgoAction.Buy;
            crossover = CrossoverStatus.CrossedUp;
            reason = $"BUY SIGNAL: MACD ({macd.MacdLine:F4}) crossed above Signal ({macd.SignalLine:F4}) - both positive";
        }
        // SELL: When MACD crosses below Signal
        else if (bearishCrossover)
        {
            action = AlgoAction.Sell;
            crossover = CrossoverStatus.CrossedDown;
            reason = $"SELL SIGNAL: MACD ({macd.MacdLine:F4}) crossed below Signal ({macd.SignalLine:F4})";
        }
        // Monitoring states
        else if (bullishCrossover)
        {
            crossover = CrossoverStatus.CrossedUp;
            reason = $"Crossed up but lines not both positive. MACD={macd.MacdLine:F4}, Signal={macd.SignalLine:F4}";
        }
        else if (macd.MacdLine > 0 && macd.SignalLine > 0 && macd.MacdLine < macd.SignalLine)
        {
            reason = $"Monitoring: Both positive, waiting for crossover. MACD={macd.MacdLine:F4}, Signal={macd.SignalLine:F4}";
        }
        else
        {
            reason = $"Monitoring: MACD={macd.MacdLine:F4}, Signal={macd.SignalLine:F4}, Histogram={macd.Histogram:F4}";
        }

        return (action, reason, crossover);
    }

    private string GetIntervalString(int intervalSeconds)
    {
        return intervalSeconds switch
        {
            15 => "15s",
            30 => "30s",
            60 => "1min",
            _ => $"{intervalSeconds}s"
        };
    }
}
