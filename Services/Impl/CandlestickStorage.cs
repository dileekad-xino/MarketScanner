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

    public void AddCandlestick(Candlestick candlestick)
    {
        var key = GetKey(candlestick.Symbol, candlestick.Interval);
        var queue = _storage.GetOrAdd(key, _ => new Queue<Candlestick>());

        lock (queue)
        {
            queue.Enqueue(candlestick);

            // Maintain rolling window - remove oldest if exceeds max
            while (queue.Count > _config.MaxCandlesticksToStore)
            {
                queue.Dequeue();
            }
        }

        _logger.LogDebug("Added candlestick for {Symbol} ({Interval}). Total stored: {Count}",
            candlestick.Symbol, candlestick.Interval, queue.Count);
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
            // Return the most recent N candlesticks in chronological order
            var candlesticks = queue.ToList();
            if (candlesticks.Count == 0)
                return Array.Empty<Candlestick>();

            // Take the last N items (most recent)
            var result = candlesticks
                .TakeLast(Math.Min(count, candlesticks.Count))
                .ToList();

            return result;
        }
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

