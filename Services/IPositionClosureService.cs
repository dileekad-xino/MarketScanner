using MarketScanner.ViewModels;

namespace MarketScanner.Services;

/// <summary>
/// Service for closing positions in running algorithms.
/// Follows Single Responsibility Principle - handles all position closure operations.
/// </summary>
public interface IPositionClosureService
{
    /// <summary>
    /// Closes a position for a single algorithm runner.
    /// </summary>
    /// <param name="algoRunner">The algorithm runner whose position should be closed.</param>
    /// <returns>Task representing the async operation.</returns>
    Task ClosePositionAsync(AlgoRunnerViewModel algoRunner);

    /// <summary>
    /// Closes positions for multiple algorithm runners.
    /// </summary>
    /// <param name="algoRunners">The collection of algorithm runners whose positions should be closed.</param>
    /// <returns>Task representing the async operation.</returns>
    Task CloseAllOpenPositionsAsync(IEnumerable<AlgoRunnerViewModel> algoRunners);
}
