using MarketScanner.Models;
using MarketScanner.ViewModels;

namespace MarketScanner.Services;

/// <summary>
/// Interface for trading algorithm strategies.
/// Implement this interface to create custom trading algorithms.
/// </summary>
public interface IAlgoStrategy
{
    /// <summary>
    /// Gets the name of the algorithm.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the description of the algorithm.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Executes the algorithm on the given symbol.
    /// </summary>
    /// <param name="symbol">The symbol to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The algorithm result containing buy/sell/hold recommendation.</returns>
    Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default);
}

