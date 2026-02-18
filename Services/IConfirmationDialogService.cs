namespace MarketScanner.Services;

/// <summary>
/// Service for displaying confirmation dialogs to users.
/// Follows Single Responsibility Principle - handles all user confirmation dialogs.
/// </summary>
public interface IConfirmationDialogService
{
    /// <summary>
    /// Shows a standardized warning/confirmation dialog.
    /// </summary>
    /// <param name="type">The warning type.</param>
    /// <param name="symbol">Optional symbol for contextual messages.</param>
    /// <returns>True if user confirms, false if user cancels.</returns>
    Task<bool> ShowWarningAsync(WarningDialogType type, string? symbol = null);

    /// <summary>
    /// Shows a confirmation dialog when the user attempts to exit the application.
    /// </summary>
    /// <param name="message">The message to display in the dialog.</param>
    /// <returns>True if user confirms exit, false if user cancels.</returns>
    Task<bool> ShowExitConfirmationAsync(string message);

    /// <summary>
    /// Shows a confirmation dialog when the user attempts to replace a running algorithm.
    /// </summary>
    /// <param name="symbol">The symbol of the algorithm that will be replaced.</param>
    /// <returns>True if user confirms replacement, false if user cancels.</returns>
    Task<bool> ShowReplaceAlgorithmConfirmationAsync(string symbol);
}
