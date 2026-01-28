using MarketScanner.Services;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Implementation of IPositionClosureService.
/// Delegates position closure to AlgoRunnerViewModel and handles batch operations.
/// </summary>
public class PositionClosureService : IPositionClosureService
{
    private readonly ILogger<PositionClosureService> _logger;

    public PositionClosureService(ILogger<PositionClosureService> logger)
    {
        _logger = logger;
    }

    public async Task ClosePositionAsync(AlgoRunnerViewModel algoRunner)
    {
        if (algoRunner == null)
        {
            _logger.LogWarning("Attempted to close position for null AlgoRunnerViewModel");
            return;
        }

        try
        {
            var symbol = algoRunner.SelectedSymbol?.Symbol ?? "Unknown";
            
            // Check if there's actually a position to close
            if (!algoRunner.HasPosition || algoRunner.PositionClosed)
            {
                _logger.LogInformation("No open position to close for {Symbol}", symbol);
                return;
            }

            _logger.LogInformation("Closing position for {Symbol}", symbol);
            await algoRunner.ClosePositionAsync();
            _logger.LogInformation("Position closed successfully for {Symbol}", symbol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing position for {Symbol}", 
                algoRunner.SelectedSymbol?.Symbol ?? "Unknown");
            // Don't throw - allow operation to continue even if position closure fails
        }
    }

    public async Task CloseAllOpenPositionsAsync(IEnumerable<AlgoRunnerViewModel> algoRunners)
    {
        if (algoRunners == null)
        {
            _logger.LogWarning("Attempted to close positions for null collection");
            return;
        }

        var runnersWithPositions = algoRunners
            .Where(ar => ar != null && ar.HasPosition && !ar.PositionClosed)
            .ToList();

        if (runnersWithPositions.Count == 0)
        {
            _logger.LogInformation("No open positions to close");
            return;
        }

        _logger.LogInformation("Closing {Count} open position(s)", runnersWithPositions.Count);

        // Close all positions in parallel for better performance
        var closeTasks = runnersWithPositions.Select(ar => ClosePositionAsync(ar));
        
        try
        {
            await Task.WhenAll(closeTasks);
            _logger.LogInformation("All positions closed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing some positions - {Count} position(s) processed", 
                runnersWithPositions.Count);
            // Don't throw - log errors but continue
        }
    }
}
