using MarketScanner.Models;
using System.Collections.Concurrent;

namespace MarketScanner.Services;

/// <summary>
/// Thread-safe service for tracking trading positions.
/// </summary>
public sealed class PositionTrackingService : IPositionTrackingService
{
    // Key: Symbol (one position per symbol)
    private readonly ConcurrentDictionary<string, PositionTracker> _openPositions = new();
    private readonly ConcurrentBag<PositionResult> _completedPositions = new();

    public event EventHandler<PositionResult>? PositionClosed;

    public void OpenPosition(PositionTracker position)
    {
        if (position == null) throw new ArgumentNullException(nameof(position));
        
        // If position already exists for this symbol, close it first
        if (_openPositions.TryGetValue(position.Symbol, out var existing))
        {
            // Close existing position with "Replaced" reason
            ClosePosition(
                position.Symbol,
                "REPLACED",
                existing.CurrentPrice,
                existing.CurrentRsi,
                $"Position replaced by new {position.EntrySignal} signal");
        }

        _openPositions[position.Symbol] = position;
    }

    public IReadOnlyList<PositionTracker> GetOpenPositions()
    {
        return _openPositions.Values.Where(p => p.IsOpen).ToList();
    }

    public IReadOnlyList<PositionResult> GetCompletedPositions()
    {
        return _completedPositions.ToList();
    }

    public PositionTracker? GetOpenPosition(string symbol)
    {
        if (_openPositions.TryGetValue(symbol, out var position) && position.IsOpen)
        {
            return position;
        }
        return null;
    }

    public void ClosePosition(string symbol, string exitSignal, double exitPrice, double exitRsi, string exitReason)
    {
        if (!_openPositions.TryRemove(symbol, out var position))
        {
            return; // Position not found
        }

        if (!position.IsOpen)
        {
            return; // Already closed
        }

        position.Close(exitSignal, exitPrice, exitRsi, exitReason);
        var result = PositionResult.FromTracker(position);
        _completedPositions.Add(result);

        PositionClosed?.Invoke(this, result);
    }

    public void ClearAll()
    {
        _openPositions.Clear();
        _completedPositions.Clear();
    }
}

