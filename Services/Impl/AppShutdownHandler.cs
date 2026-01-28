using MarketScanner.Services;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Implementation of IAppShutdownHandler.
/// Orchestrates the shutdown flow: check for running algos → confirm → close positions → shutdown.
/// </summary>
public class AppShutdownHandler : IAppShutdownHandler
{
    private readonly AlgoRunnerManagerService _algoRunnerManagerService;
    private readonly IConfirmationDialogService _confirmationDialogService;
    private readonly IPositionClosureService _positionClosureService;
    private readonly ILogger<AppShutdownHandler> _logger;

    public AppShutdownHandler(
        AlgoRunnerManagerService algoRunnerManagerService,
        IConfirmationDialogService confirmationDialogService,
        IPositionClosureService positionClosureService,
        ILogger<AppShutdownHandler> logger)
    {
        _algoRunnerManagerService = algoRunnerManagerService ?? 
            throw new ArgumentNullException(nameof(algoRunnerManagerService));
        _confirmationDialogService = confirmationDialogService ?? 
            throw new ArgumentNullException(nameof(confirmationDialogService));
        _positionClosureService = positionClosureService ?? 
            throw new ArgumentNullException(nameof(positionClosureService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> HandleShutdownAsync()
    {
        try
        {
            // Check if there are running algorithms with open positions
            var runningAlgosWithPositions = _algoRunnerManagerService.AlgoRunners
                .Where(ar => ar.IsRunning && ar.HasPosition && !ar.PositionClosed)
                .ToList();

            if (runningAlgosWithPositions.Count == 0)
            {
                _logger.LogInformation("No running algorithms with open positions - proceeding with shutdown");
                return true; // No positions to close, proceed with shutdown
            }

            _logger.LogInformation("Found {Count} running algorithm(s) with open positions", 
                runningAlgosWithPositions.Count);

            // Show confirmation dialog
            var message = "Are you sure you want to exit? The current running algos position will be closed.";
            var shouldExit = await _confirmationDialogService.ShowExitConfirmationAsync(message);

            if (!shouldExit)
            {
                _logger.LogInformation("User cancelled app shutdown");
                return false; // User cancelled, prevent shutdown
            }

            // User confirmed - close all open positions
            _logger.LogInformation("User confirmed shutdown - closing {Count} open position(s)", 
                runningAlgosWithPositions.Count);
            
            await _positionClosureService.CloseAllOpenPositionsAsync(runningAlgosWithPositions);

            _logger.LogInformation("Shutdown handler completed - proceeding with app shutdown");
            return true; // Proceed with shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in shutdown handler - defaulting to allow shutdown");
            // On error, default to allowing shutdown to prevent app from getting stuck
            return true;
        }
    }
}
