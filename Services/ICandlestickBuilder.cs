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
    /// Starts the candlestick builder by subscribing to tick stream.
    /// </summary>
    void Start();

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
}

