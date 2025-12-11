using MarketScanner.Config;
using MarketScanner.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// In-memory storage for candlesticks with rolling window management.
/// </summary>
public class CandlestickStorage : ICandlestickStorage
{
    private readonly CandlestickConfig _config;
    private readonly ILogger<CandlestickStorage> _logger;

    // Storage: Dictionary key is "{symbol}_{interval}" -> Queue of candlesticks
    private readonly ConcurrentDictionary<string, Queue<Candlestick>> _storage = new();

    public CandlestickStorage(
        CandlestickConfig config,
        ILogger<CandlestickStorage> logger)
    {
        _config = config;
        _logger = logger;
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
        
        // DateTimeKind.Unspecified - assume it's already in UTC or treat as UTC
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    /// <summary>
    /// Normalizes to UTC and truncates to the nearest interval boundary (floor).
    /// Result is always DateTimeKind.Utc and aligned to interval boundaries.
    /// </summary>
    private DateTime NormalizeAndTruncateToInterval(DateTime dt)
    {
        var utc = NormalizeToUtc(dt);
        var interval = TimeSpan.FromSeconds(_config.IntervalSeconds).Ticks;
        var truncatedTicks = (utc.Ticks / interval) * interval;
        return new DateTime(truncatedTicks, DateTimeKind.Utc);
    }

    public void AddCandlestick(Candlestick candlestick)
    {
        // Normalize to UTC and truncate to interval boundary
        var utc = NormalizeToUtc(candlestick.Timestamp);
        var truncated = NormalizeAndTruncateToInterval(utc);
        
        // Create new candlestick with truncated UTC timestamp
        var normalizedCandlestick = candlestick with { Timestamp = truncated };

        // Diagnostic logging
        _logger.LogDebug(
            "CandlestickStorage: Add TS TRACE - raw={Raw:o}, utc={Utc:o}, truncated={Trunc:o}",
            candlestick.Timestamp,
            utc,
            truncated);

        var key = GetKey(normalizedCandlestick.Symbol, normalizedCandlestick.Interval);
        var queue = _storage.GetOrAdd(key, _ => new Queue<Candlestick>());

        lock (queue)
        {
            queue.Enqueue(normalizedCandlestick);

            // Maintain rolling window - remove oldest if exceeds max
            while (queue.Count > _config.MaxCandlesticksToStore)
            {
                queue.Dequeue();
            }
        }

        _logger.LogInformation("Added candlestick for {Symbol} ({Interval}). Total stored: {Count}, Timestamp={Time:o} (Kind={Kind})",
            normalizedCandlestick.Symbol, normalizedCandlestick.Interval, queue.Count, normalizedCandlestick.Timestamp, normalizedCandlestick.Timestamp.Kind);
    }

    public IReadOnlyList<Candlestick> GetCandlesticks(string symbol, string interval, int count)
    {
        var key = GetKey(symbol, interval);
        if (!_storage.TryGetValue(key, out var queue))
        {
            return Array.Empty<Candlestick>();
        }

        lock (queue)
        {
            // Ensure all timestamps are UTC and truncated (should already be from AddCandlestick, but verify)
            var candlesticks = queue
                .Select(c => 
                {
                    // Safety check: ensure UTC and truncated (should already be from AddCandlestick)
                    if (c.Timestamp.Kind != DateTimeKind.Utc || !IsTruncated(c.Timestamp))
                    {
                        var truncated = NormalizeAndTruncateToInterval(c.Timestamp);
                        return c with { Timestamp = truncated };
                    }
                    return c;
                })
                .OrderBy(c => c.Timestamp)
                .ToList();
            
            if (candlesticks.Count == 0)
                return Array.Empty<Candlestick>();

            // Diagnostic logging
            _logger.LogDebug("GetCandlesticks: Returning {Count} candles for {Symbol} ({Interval}), First={First:o}, Last={Last:o}",
                candlesticks.Count, symbol, interval,
                candlesticks.First().Timestamp,
                candlesticks.Last().Timestamp);

            // *** FIX ***
            // Always return ALL candles. The strategy will handle trimming.
            // This ensures live finalized candles are always visible to MACD.
            return new List<Candlestick>(candlesticks);
        }
    }

    /// <summary>
    /// Checks if a timestamp is already truncated to the interval boundary.
    /// </summary>
    private bool IsTruncated(DateTime dt)
    {
        if (dt.Kind != DateTimeKind.Utc)
            return false;
        
        var interval = TimeSpan.FromSeconds(_config.IntervalSeconds).Ticks;
        return (dt.Ticks % interval) == 0;
    }

    public Candlestick? GetLatestCandlestick(string symbol, string interval)
    {
        var key = GetKey(symbol, interval);
        if (!_storage.TryGetValue(key, out var queue))
        {
            return null;
        }

        lock (queue)
        {
            return queue.Count > 0 ? queue.Last() : null;
        }
    }

    public void Clear(string symbol)
    {
        var keysToRemove = _storage.Keys
            .Where(k => k.StartsWith($"{symbol}_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            _storage.TryRemove(key, out _);
        }

        _logger.LogDebug("Cleared candlesticks for symbol {Symbol}", symbol);
    }

    public void ClearAll()
    {
        _storage.Clear();
        _logger.LogDebug("Cleared all candlesticks");
    }

    private static string GetKey(string symbol, string interval)
    {
        return $"{symbol}_{interval}";
    }
}

