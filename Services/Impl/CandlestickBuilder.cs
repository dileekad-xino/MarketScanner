using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Services.Ibkr;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Builds candlesticks from tick data by aggregating ticks into time-based intervals.
/// Adds optional polling fallback to fetch latest historical bars periodically
/// (useful when live tick timestamps are unreliable or missing Kind information).
/// </summary>
public class CandlestickBuilder : ICandlestickBuilder, IDisposable
{
    private readonly IbkrGatewayService _ibkrGatewayService;
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _config;
    private readonly ILogger<CandlestickBuilder> _logger;
    private readonly Subject<Candlestick> _candlestickSubject = new();
    private IDisposable? _tickSubscription;
    private bool _disposed;

    // Polling
    private Task? _pollingTask;
    private CancellationTokenSource? _pollingCts;

    // Track subscribed symbols (only process ticks / polls for these)
    private readonly ConcurrentDictionary<string, bool> _subscribedSymbols = new();

    // Track current candlesticks being built per symbol
    private readonly ConcurrentDictionary<string, InProgressCandlestick> _inProgress = new();

    // Track the last interval boundary time per symbol
    private readonly ConcurrentDictionary<string, DateTime> _lastIntervalBoundary = new();

    public IObservable<Candlestick> CandlestickStream => _candlestickSubject.AsObservable();

    public CandlestickBuilder(
        IbkrGatewayService ibkrGatewayService,
        ICandlestickStorage candlestickStorage,
        CandlestickConfig config,
        ILogger<CandlestickBuilder> logger)
    {
        _ibkrGatewayService = ibkrGatewayService;
        _candlestickStorage = candlestickStorage;
        _config = config;
        _logger = logger;
    }

    public void Start()
    {
        if (_tickSubscription != null)
        {
            _logger.LogWarning("CandlestickBuilder is already started");
            return;
        }

        _logger.LogInformation("Starting CandlestickBuilder with interval: {IntervalSeconds}s", _config.IntervalSeconds);

        // Subscribe to live ticks
        _tickSubscription = _ibkrGatewayService.TickStream
            .Subscribe(OnTick);

        // Start polling fallback if enabled in config (or default to false)
        var enablePolling = TryGetConfigValue(nameof(_config.EnablePollingFallback), defaultValue: false);
        var pollingInterval = TryGetConfigValue(nameof(_config.PollingIntervalSeconds), defaultValue: 10);

        if (enablePolling)
        {
            StartPolling(pollingInterval);
            _logger.LogInformation("CandlestickBuilder: Polling fallback enabled (interval {Seconds}s)", pollingInterval);
        }
        else
        {
            _logger.LogInformation("CandlestickBuilder: Polling fallback disabled");
        }

        _logger.LogInformation("CandlestickBuilder started successfully");
    }

    public void Stop()
    {
        _tickSubscription?.Dispose();
        _tickSubscription = null;
        StopPolling();
        _logger.LogInformation("CandlestickBuilder stopped");
    }

    private void OnTick(TickData tick)
    {
        if (tick.LastPrice == null || tick.LastPrice <= 0)
            return;

        var symbol = tick.Symbol;

        // Only process ticks for subscribed symbols
        if (!_subscribedSymbols.ContainsKey(symbol))
            return;

        var price = (decimal)tick.LastPrice.Value;
        var volume = tick.Volume ?? 0;

        // --- Normalize tick timestamp to UTC immediately (critical) ---
        var timestamp = tick.Timestamp;
        if (timestamp.Kind == DateTimeKind.Local)
            timestamp = timestamp.ToUniversalTime();
        else if (timestamp.Kind == DateTimeKind.Unspecified)
            timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

        // Calculate the interval boundary for this tick (truncated to interval)
        var intervalBoundary = NormalizeAndTruncateToInterval(timestamp);

        // Diagnostic logging
        _logger.LogDebug(
            "CandlestickBuilder: TS TRACE - raw={Raw:o}, normalized={Utc:o}, truncated={Trunc:o}",
            tick.Timestamp,
            timestamp,
            intervalBoundary);

        _logger.LogInformation("CandlestickBuilder: Tick for {Symbol}: Price={Price}, Time={Time:o}",
            symbol, price, timestamp);

        // Check if we need to finalize previous candlestick
        if (_lastIntervalBoundary.TryGetValue(symbol, out var lastBoundary))
        {
            if (intervalBoundary > lastBoundary)
            {
                // New interval started - finalize previous candlestick
                _logger.LogInformation("CandlestickBuilder: Interval boundary crossed for {Symbol}, finalizing candlestick", symbol);
                FinalizeCandlestick(symbol, lastBoundary);
            }
        }
        else
        {
            // First tick for this symbol - initialize boundary
            _lastIntervalBoundary[symbol] = intervalBoundary;
        }

        // Update or create in-progress candlestick
        var inProgress = _inProgress.GetOrAdd(symbol, _ => new InProgressCandlestick
        {
            Symbol = symbol,
            Interval = GetIntervalString(_config.IntervalSeconds),
            StartTime = intervalBoundary
        });

        // Update candlestick data
        if (inProgress.Open == null)
        {
            inProgress.Open = price;
        }
        inProgress.High = Math.Max(inProgress.High ?? price, price);
        inProgress.Low = Math.Min(inProgress.Low ?? price, price);
        inProgress.Close = price;
        inProgress.Volume += volume;
        inProgress.LastUpdate = timestamp;

        // Update interval boundary
        _lastIntervalBoundary[symbol] = intervalBoundary;
    }

    private void FinalizeCandlestick(string symbol, DateTime intervalBoundary)
    {
        if (!_inProgress.TryRemove(symbol, out var inProgress))
            return;

        if (inProgress.Open == null)
            return;

        // Always truncate to interval boundary (should already be truncated, but ensure it)
        var truncated = NormalizeAndTruncateToInterval(intervalBoundary);

        var candlestick = new Candlestick(
            Symbol: symbol,
            Open: inProgress.Open.Value,
            High: inProgress.High ?? inProgress.Open.Value,
            Low: inProgress.Low ?? inProgress.Open.Value,
            Close: inProgress.Close ?? inProgress.Open.Value,
            Volume: inProgress.Volume,
            Timestamp: truncated,
            Interval: inProgress.Interval
        );

        // Diagnostic logging
        _logger.LogDebug(
            "CandlestickBuilder: Finalize TS TRACE - raw={Raw:o}, utc={Utc:o}, truncated={Trunc:o}",
            intervalBoundary,
            NormalizeToUtc(intervalBoundary),
            truncated);

        _logger.LogInformation("Finalized candlestick for {Symbol}: O={Open}, H={High}, L={Low}, C={Close}, V={Volume}, Time={Time} (Kind={Kind})",
            symbol, candlestick.Open, candlestick.High, candlestick.Low, candlestick.Close, candlestick.Volume, candlestick.Timestamp, candlestick.Timestamp.Kind);

        // Add to storage so it's available for MACD calculations
        // Storage will keep the rolling window and normalize again for safety
        _candlestickStorage.AddCandlestick(candlestick);

        // Publish to observable stream for subscribers
        _candlestickSubject.OnNext(candlestick);
    }

    /// <summary>
    /// Starts a polling background task that periodically fetches the latest historical bars
    /// for all subscribed symbols. This is a fallback for cases where live tick timestamps
    /// are unreliable or lost.
    /// </summary>
    private void StartPolling(int pollingIntervalSeconds)
    {
        if (_pollingTask != null && !_pollingTask.IsCompleted)
            return;

        _pollingCts = new CancellationTokenSource();
        var ct = _pollingCts.Token;

        // Number of bars to fetch per request - small (we only need the latest bars)
        var barsToFetch = TryGetConfigValue(nameof(_config.PollingBarsToFetch), defaultValue: 3);

        _pollingTask = Task.Run(async () =>
        {
            _logger.LogInformation("CandlestickBuilder: Polling task started (interval {Sec}s)", pollingIntervalSeconds);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Snapshot keys to avoid collection modifications during enumeration
                    var symbols = _subscribedSymbols.Keys.ToList();

                    if (symbols.Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(pollingIntervalSeconds), ct);
                        continue;
                    }

                    foreach (var symbol in symbols)
                    {
                        if (ct.IsCancellationRequested) break;

                        try
                        {
                            // Ask for a small number of latest bars (IBKR returns UTC-based bars)
                            var bars = await _ibkrGatewayService.GetHistoricalBarsAsync(
                                symbol,
                                _config.IntervalSeconds,
                                barsToFetch,
                                ct);

                            if (bars != null && bars.Count > 0)
                            {
                                // Get the latest candlestick ONCE before processing all bars
                                var storedLatest = _candlestickStorage.GetLatestCandlestick(symbol, GetIntervalString(_config.IntervalSeconds));
                                var storedLatestTrunc = storedLatest != null 
                                    ? NormalizeAndTruncateToInterval(storedLatest.Timestamp) 
                                    : DateTime.MinValue;

                                _logger.LogInformation("CandlestickBuilder(POLL): Processing {Count} bars for {Symbol}, storedLatest={Stored:o}", 
                                    bars.Count, symbol, storedLatest?.Timestamp);

                                // Process bars in chronological order (older -> newer)
                                foreach (var bar in bars.OrderBy(b => b.Timestamp))
                                {
                                    // Normalize to UTC and truncate to interval boundary
                                    var normalizedTrunc = NormalizeAndTruncateToInterval(bar.Timestamp);

                                    // CRITICAL FIX: Only add if this bar is NEWER than stored latest (not same timestamp)
                                    // Same-timestamp bars should NOT be added - they're updates to the current bar, not new bars
                                    // Adding same-timestamp bars causes duplicates and incorrect MACD calculations
                                    var isNewer = normalizedTrunc > storedLatestTrunc;
                                    
                                    if (isNewer)
                                    {
                                        var candlestick = new Candlestick(
                                            Symbol: bar.Symbol,
                                            Open: bar.Open,
                                            High: bar.High,
                                            Low: bar.Low,
                                            Close: bar.Close,
                                            Volume: bar.Volume,
                                            Timestamp: normalizedTrunc,
                                            Interval: GetIntervalString(_config.IntervalSeconds)
                                        );

                                        _logger.LogInformation("CandlestickBuilder(POLL): Adding NEW candlestick for {Symbol} time={Time:o} (from polling, storedLatest={Stored:o})", 
                                            candlestick.Symbol, candlestick.Timestamp, storedLatest?.Timestamp);
                                        
                                        _candlestickStorage.AddCandlestick(candlestick);
                                        _candlestickSubject.OnNext(candlestick);
                                        
                                        // Update storedLatestTrunc for next iteration (but don't call GetLatestCandlestick again)
                                        storedLatestTrunc = normalizedTrunc;
                                    }
                                    else
                                    {
                                        _logger.LogDebug("CandlestickBuilder(POLL): Skipping bar for {Symbol} time={Time:o} (not newer than storedLatest={Stored:o})", 
                                            symbol, normalizedTrunc, storedLatest?.Timestamp);
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { /* shutting down */ }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "CandlestickBuilder: Polling failed for {Symbol}", symbol);
                        }

                        // Stagger requests lightly to avoid bursts when many symbols subscribed
                        await Task.Delay(200, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CandlestickBuilder: Unexpected polling error");
                }

                await Task.Delay(TimeSpan.FromSeconds(pollingIntervalSeconds), ct);
            }

            _logger.LogInformation("CandlestickBuilder: Polling task stopped");
        }, ct);
    }

    private void StopPolling()
    {
        try
        {
            _pollingCts?.Cancel();
            _pollingTask = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CandlestickBuilder: Error stopping polling");
        }
    }

    /// <summary>
    /// Normalizes a DateTime to UTC, handling all DateTimeKind values.
    /// </summary>
    private DateTime NormalizeToUtc(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Utc)
            return dt;

        if (dt.Kind == DateTimeKind.Local)
            return dt.ToUniversalTime();

        // DateTimeKind.Unspecified - assume it's UTC (IBKR sometimes strips Kind)
        // If you prefer, you can try to detect local vs utc by heuristics; explicit assumption is safer.
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    /// <summary>
    /// Normalizes to UTC and truncates to the nearest interval boundary (floor).
    /// Result is always DateTimeKind.Utc and aligned to interval boundaries.
    /// </summary>
    private DateTime NormalizeAndTruncateToInterval(DateTime dt)
    {
        var utc = NormalizeToUtc(dt);
        var intervalTicks = TimeSpan.FromSeconds(_config.IntervalSeconds).Ticks;
        var truncatedTicks = (utc.Ticks / intervalTicks) * intervalTicks;
        return new DateTime(truncatedTicks, DateTimeKind.Utc);
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

    public void SubscribeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        _subscribedSymbols.TryAdd(symbol, true);
        _logger.LogInformation("Subscribed to candlestick building for {Symbol}", symbol);
    }

    public void UnsubscribeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        _subscribedSymbols.TryRemove(symbol, out _);

        // Clean up any in-progress candlesticks and tracking data
        _inProgress.TryRemove(symbol, out _);
        _lastIntervalBoundary.TryRemove(symbol, out _);

        _logger.LogInformation("Unsubscribed from candlestick building for {Symbol}", symbol);
    }

    public bool IsSubscribed(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return false;

        return _subscribedSymbols.ContainsKey(symbol);
    }

    public async Task PreloadCandlesticksAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        // Use configured preload count, with fallback to minimum required
        var minRequired = _config.Macd.SlowPeriod + _config.Macd.SignalPeriod;
        var barsToFetch = _config.HistoricalPreloadCount > 0
            ? _config.HistoricalPreloadCount
            : Math.Max(minRequired + 15, 200); // Fallback to at least 200

        _logger.LogInformation("PreloadCandlesticksAsync: Fetching {Count} historical bars for {Symbol} ({Interval}s)",
            barsToFetch, symbol, _config.IntervalSeconds);

        try
        {
            var bars = await _ibkrGatewayService.GetHistoricalBarsAsync(
                symbol,
                _config.IntervalSeconds,
                barsToFetch,
                ct);

            if (bars.Count == 0)
            {
                _logger.LogWarning("PreloadCandlesticksAsync: No historical bars returned for {Symbol}", symbol);
                return;
            }

            // Add candlesticks to storage
            foreach (var candle in bars)
            {
                // Normalize/truncate bar timestamp before adding
                var trunc = NormalizeAndTruncateToInterval(candle.Timestamp);
                var normalizedBar = candle with { Timestamp = trunc };
                _candlestickStorage.AddCandlestick(normalizedBar);
            }

            _logger.LogInformation("PreloadCandlesticksAsync: Loaded {Count} historical candlesticks for {Symbol}",
                bars.Count, symbol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreloadCandlesticksAsync: Failed to preload candlesticks for {Symbol}", symbol);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _candlestickSubject.Dispose();
        _disposed = true;
    }

    private class InProgressCandlestick
    {
        public string Symbol { get; set; } = string.Empty;
        public string Interval { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public decimal? Open { get; set; }
        public decimal? High { get; set; }
        public decimal? Low { get; set; }
        public decimal? Close { get; set; }
        public long Volume { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    // Helper to safely read config values (avoids compile errors if new props absent)
    private T TryGetConfigValue<T>(string propName, T defaultValue)
    {
        try
        {
            var prop = typeof(CandlestickConfig).GetProperty(propName);
            if (prop != null && prop.GetValue(_config) is T val)
                return val;
        }
        catch { /* ignore reflection issues */ }

        return defaultValue;
    }
}
