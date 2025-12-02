using MarketScanner.Models;

namespace MarketScanner.Services;

/// <summary>
/// Service for tracking trading positions and monitoring them until exit conditions are met.
/// </summary>
public interface IPositionTrackingService
{
    /// <summary>
    /// Opens a new position for tracking
    /// </summary>
    void OpenPosition(PositionTracker position);

    /// <summary>
    /// Gets all open positions
    /// </summary>
    IReadOnlyList<PositionTracker> GetOpenPositions();

    /// <summary>
    /// Gets all completed positions (trade results)
    /// </summary>
    IReadOnlyList<PositionResult> GetCompletedPositions();

    /// <summary>
    /// Gets a specific open position by symbol
    /// </summary>
    PositionTracker? GetOpenPosition(string symbol);

    /// <summary>
    /// Closes a position and moves it to completed results
    /// </summary>
    void ClosePosition(string symbol, string exitSignal, double exitPrice, double exitRsi, string exitReason);

    /// <summary>
    /// Clears all positions (for reset/testing)
    /// </summary>
    void ClearAll();

    /// <summary>
    /// Event fired when a position is closed
    /// </summary>
    event EventHandler<PositionResult>? PositionClosed;
}

