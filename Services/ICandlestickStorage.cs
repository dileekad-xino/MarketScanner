using MarketScanner.Models;

namespace MarketScanner.Services;

/// <summary>
/// Service for storing and retrieving candlesticks.
/// </summary>
public interface ICandlestickStorage
{
    /// <summary>
    /// Adds a candlestick to storage.
    /// </summary>
    void AddCandlestick(Candlestick candlestick);

    /// <summary>
    /// Gets the most recent candlesticks for a symbol and interval.
    /// </summary>
    /// <param name="symbol">The symbol</param>
    /// <param name="interval">The interval (e.g., "30s", "1min")</param>
    /// <param name="count">Number of candlesticks to retrieve</param>
    /// <returns>List of candlesticks in chronological order (oldest first)</returns>
    IReadOnlyList<Candlestick> GetCandlesticks(string symbol, string interval, int count);

    /// <summary>
    /// Gets the latest candlestick for a symbol and interval.
    /// </summary>
    Candlestick? GetLatestCandlestick(string symbol, string interval);

    /// <summary>
    /// Clears all stored candlesticks for a symbol.
    /// </summary>
    void Clear(string symbol);

    /// <summary>
    /// Clears all stored candlesticks.
    /// </summary>
    void ClearAll();
}

