using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Services.Ibkr;
using MarketScanner.Utilities;
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
    private IDisposable? _streamingBarSubscription;
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
    
    public event Action<string, Candlestick>? OnLiveCandleUpdated;
    public event Action<string, decimal, DateTime>? OnTickPrice;
    public event Action<string, Candlestick>? OnFinalizedCandle;

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

    public void Start(bool useStreamingBars = false)
    {
        if (_tickSubscription != null || _streamingBarSubscription != null)
        {
            _logger.LogWarning("CandlestickBuilder is already started");
            return;
        }

        _logger.LogInformation("Starting CandlestickBuilder with interval: {IntervalSeconds}s, useStreamingBars={UseBars}",
            _config.IntervalSeconds, useStreamingBars);

        if (useStreamingBars)
        {
            // Subscribe to streaming historical bars instead of ticks
            _streamingBarSubscription = _ibkrGatewayService.StreamingBarStream
                .Subscribe(OnStreamingBar);
            _logger.LogInformation("CandlestickBuilder: Using streaming historical bars for updates");
        }
        else
        {
            // Original tick-based approach
            _tickSubscription = _ibkrGatewayService.TickStream
                .Subscribe(OnTick);
            _logger.LogInformation("CandlestickBuilder: Using tick data for updates");
        }

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
        _streamingBarSubscription?.Dispose();
        _streamingBarSubscription = null;
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
        var timestamp = TimestampUtils.NormalizeToUtc(tick.Timestamp);

        // Emit tick price event for live MACD/RSI updates
        OnTickPrice?.Invoke(symbol, price, timestamp);

        // Calculate the interval boundary for this tick (truncated to interval)
        var intervalBoundary = TimestampUtils.NormalizeAndTruncateToInterval(timestamp, _config.IntervalSeconds);

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

        // Emit live candle update event for real-time MACD/RSI updates
        var liveCandle = new Candlestick(
            Symbol: symbol,
            Open: inProgress.Open ?? price,
            High: inProgress.High ?? price,
            Low: inProgress.Low ?? price,
            Close: price,
            Volume: inProgress.Volume,
            Timestamp: intervalBoundary,
            Interval: inProgress.Interval
        );
        OnLiveCandleUpdated?.Invoke(symbol, liveCandle);

        // Update interval boundary
        _lastIntervalBoundary[symbol] = intervalBoundary;
    }

    private void OnStreamingBar(Candlestick bar)
    {
        var symbol = bar.Symbol;

        if (!_subscribedSymbols.ContainsKey(symbol))
            return;

        var timestamp = TimestampUtils.NormalizeToUtc(bar.Timestamp);

        // Emit bar close price for RSI compatibility (RSI still uses ticks via OnTickPrice)
        OnTickPrice?.Invoke(symbol, bar.Close, timestamp);

        // The bar already represents a complete candlestick, so we can use it directly
        // Check if this bar belongs to a new interval
        var intervalBoundary = TimestampUtils.NormalizeAndTruncateToInterval(timestamp, _config.IntervalSeconds);

        if (_lastIntervalBoundary.TryGetValue(symbol, out var lastBoundary))
        {
            if (intervalBoundary > lastBoundary)
            {
                // New interval - finalize previous candlestick if exists
                FinalizeCandlestick(symbol, lastBoundary);
            }
        }
        else
        {
            _lastIntervalBoundary[symbol] = intervalBoundary;
        }

        // For streaming bars, we can use the bar directly as the in-progress candlestick
        // or aggregate multiple bars if bar size < candlestick interval
        var inProgress = _inProgress.GetOrAdd(symbol, _ => new InProgressCandlestick
        {
            Symbol = symbol,
            Interval = GetIntervalString(_config.IntervalSeconds),
            StartTime = intervalBoundary
        });

        // Update in-progress candlestick with bar data
        if (inProgress.Open == null)
            inProgress.Open = bar.Open; // First bar sets open
        inProgress.High = Math.Max(inProgress.High ?? bar.High, bar.High);
        inProgress.Low = inProgress.Low == null ? bar.Low : Math.Min(inProgress.Low.Value, bar.Low);
        inProgress.Close = bar.Close; // Latest bar close
        inProgress.Volume += bar.Volume;
        inProgress.LastUpdate = timestamp;

        // Emit live update
        var liveCandle = new Candlestick(
            Symbol: symbol,
            Open: inProgress.Open ?? bar.Open,
            High: inProgress.High ?? bar.High,
            Low: inProgress.Low ?? bar.Low,
            Close: inProgress.Close ?? bar.Close,
            Volume: inProgress.Volume,
            Timestamp: intervalBoundary,
            Interval: inProgress.Interval
        );
        OnLiveCandleUpdated?.Invoke(symbol, liveCandle);
    }

    private void FinalizeCandlestick(string symbol, DateTime intervalBoundary)
    {
        if (!_inProgress.TryRemove(symbol, out var inProgress))
            return;

        if (inProgress.Open == null)
            return;

        // Always truncate to interval boundary (should already be truncated, but ensure it)
        var truncated = TimestampUtils.NormalizeAndTruncateToInterval(intervalBoundary, _config.IntervalSeconds);

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
            TimestampUtils.NormalizeToUtc(intervalBoundary),
            truncated);

        _logger.LogInformation("Finalized candlestick for {Symbol}: O={Open}, H={High}, L={Low}, C={Close}, V={Volume}, Time={Time} (Kind={Kind})",
            symbol, candlestick.Open, candlestick.High, candlestick.Low, candlestick.Close, candlestick.Volume, candlestick.Timestamp, candlestick.Timestamp.Kind);

        // Add to storage so it's available for MACD calculations
        // Storage will keep the rolling window and normalize again for safety
        _candlestickStorage.AddCandlestick(candlestick);

        // Publish to observable stream for subscribers
        _candlestickSubject.OnNext(candlestick);

        // Emit finalized candle event for MACD engine to replace tick updates
        OnFinalizedCandle?.Invoke(symbol, candlestick);
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
                                    ? TimestampUtils.NormalizeAndTruncateToInterval(storedLatest.Timestamp, _config.IntervalSeconds) 
                                    : DateTime.MinValue;

                                _logger.LogInformation("CandlestickBuilder(POLL): Processing {Count} bars for {Symbol}, storedLatest={Stored:o}", 
                                    bars.Count, symbol, storedLatest?.Timestamp);

                                // Process bars in chronological order (older -> newer)
                                foreach (var bar in bars.OrderBy(b => b.Timestamp))
                                {
                                    // Normalize to UTC and truncate to interval boundary
                                    var normalizedTrunc = TimestampUtils.NormalizeAndTruncateToInterval(bar.Timestamp, _config.IntervalSeconds);

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


    private string GetIntervalString(int intervalSeconds)
    {
        return TimeframeMap.ToIntervalKey(intervalSeconds);
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
                var trunc = TimestampUtils.NormalizeAndTruncateToInterval(candle.Timestamp, _config.IntervalSeconds);
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
