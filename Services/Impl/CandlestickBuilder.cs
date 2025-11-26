using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Services.Ibkr;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Builds candlesticks from tick data by aggregating ticks into time-based intervals.
/// Only processes ticks for subscribed symbols to optimize resource usage.
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

    // Track subscribed symbols (only process ticks for these)
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

        _tickSubscription = _ibkrGatewayService.TickStream
            .Subscribe(OnTick);

        _logger.LogInformation("CandlestickBuilder started successfully");
    }

    public void Stop()
    {
        _tickSubscription?.Dispose();
        _tickSubscription = null;
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
        var timestamp = tick.Timestamp;

        // Calculate the interval boundary for this tick
        var intervalBoundary = GetIntervalBoundary(timestamp, _config.IntervalSeconds);

        // Check if we need to finalize previous candlestick
        if (_lastIntervalBoundary.TryGetValue(symbol, out var lastBoundary))
        {
            if (intervalBoundary > lastBoundary)
            {
                // New interval started - finalize previous candlestick
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

        var candlestick = new Candlestick(
            Symbol: symbol,
            Open: inProgress.Open.Value,
            High: inProgress.High ?? inProgress.Open.Value,
            Low: inProgress.Low ?? inProgress.Open.Value,
            Close: inProgress.Close ?? inProgress.Open.Value,
            Volume: inProgress.Volume,
            Timestamp: intervalBoundary,
            Interval: inProgress.Interval
        );

        _logger.LogDebug("Finalized candlestick for {Symbol}: O={Open}, H={High}, L={Low}, C={Close}, V={Volume}",
            symbol, candlestick.Open, candlestick.High, candlestick.Low, candlestick.Close, candlestick.Volume);

        _candlestickSubject.OnNext(candlestick);
    }

    private DateTime GetIntervalBoundary(DateTime timestamp, int intervalSeconds)
    {
        // Round down to the nearest interval boundary
        var totalSeconds = (long)(timestamp - DateTime.MinValue).TotalSeconds;
        var intervalSecondsLong = (long)intervalSeconds;
        var roundedSeconds = (totalSeconds / intervalSecondsLong) * intervalSecondsLong;
        return DateTime.MinValue.AddSeconds(roundedSeconds);
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

        // Calculate how many bars we need for MACD (slowPeriod + signalPeriod + buffer)
        var minRequired = _config.Macd.SlowPeriod + _config.Macd.SignalPeriod;
        var barsToFetch = minRequired + 15; // Extra buffer

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
                _candlestickStorage.AddCandlestick(candle);
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
}

