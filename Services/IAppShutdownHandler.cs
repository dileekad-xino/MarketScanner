namespace MarketScanner.Services;

/// <summary>
/// Service for handling application shutdown logic.
/// Follows Single Responsibility Principle - handles all app shutdown concerns.
/// </summary>
public interface IAppShutdownHandler
{
    /// <summary>
    /// Handles the application shutdown process.
    /// Checks for running algorithms with open positions, shows confirmation dialog,
    /// and closes positions if user confirms.
    /// </summary>
    /// <returns>True if shutdown should proceed, false if user cancelled.</returns>
    Task<bool> HandleShutdownAsync();

    /// <summary>
    /// Shutdown without UI (e.g. when main window is closing).
    /// Closes open positions and does not show confirmation.
    /// </summary>
    Task HandleShutdownQuietAsync();
}
