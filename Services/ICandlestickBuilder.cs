using MarketScanner.Models;
using System.Reactive;

namespace MarketScanner.Services;

/// <summary>
/// Service for building candlesticks from tick data streams.
/// Only processes ticks for subscribed symbols to optimize resource usage.
/// </summary>
public interface ICandlestickBuilder
{
    /// <summary>
    /// Observable stream of completed candlesticks.
    /// </summary>
    IObservable<Candlestick> CandlestickStream { get; }

    /// <summary>
    /// Starts the candlestick builder by subscribing to tick stream or streaming bars.
    /// </summary>
    /// <param name="useStreamingBars">If true, uses streaming historical bars instead of tick data</param>
    void Start(bool useStreamingBars = false);

    /// <summary>
    /// Stops the candlestick builder.
    /// </summary>
    void Stop();

    /// <summary>
    /// Subscribes to a symbol to start building candlesticks for it.
    /// Only ticks for subscribed symbols will be processed.
    /// </summary>
    /// <param name="symbol">The symbol to subscribe to</param>
    void SubscribeSymbol(string symbol);

    /// <summary>
    /// Unsubscribes from a symbol to stop building candlesticks.
    /// Cleans up any in-progress candlesticks for the symbol.
    /// </summary>
    /// <param name="symbol">The symbol to unsubscribe from</param>
    void UnsubscribeSymbol(string symbol);

    /// <summary>
    /// Checks if a symbol is currently subscribed.
    /// </summary>
    /// <param name="symbol">The symbol to check</param>
    /// <returns>True if the symbol is subscribed, false otherwise</returns>
    bool IsSubscribed(string symbol);

    /// <summary>
    /// Preloads historical candlesticks for a symbol from IBKR.
    /// This seeds the storage so MACD can calculate immediately.
    /// </summary>
    /// <param name="symbol">The symbol to preload candlesticks for</param>
    /// <param name="ct">Cancellation token</param>
    Task PreloadCandlesticksAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Event fired when an in-progress candlestick is updated with new tick data.
    /// Fires on every tick to enable live MACD/RSI updates.
    /// </summary>
    event Action<string, Candlestick>? OnLiveCandleUpdated;

    /// <summary>
    /// Event fired on every tick with the price and timestamp.
    /// Used for live MACD/RSI updates.
    /// </summary>
    event Action<string, decimal, DateTime>? OnTickPrice;

    /// <summary>
    /// Event fired when a candlestick is finalized (interval boundary crossed).
    /// Used to replace tick-based updates with final candle close price.
    /// </summary>
    event Action<string, Candlestick>? OnFinalizedCandle;
}

