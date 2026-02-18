using MarketScanner.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Implementation of IConfirmationDialogService using MAUI DisplayAlert.
/// </summary>
public class ConfirmationDialogService : IConfirmationDialogService
{
    private readonly ILogger<ConfirmationDialogService> _logger;

    public ConfirmationDialogService(ILogger<ConfirmationDialogService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> ShowWarningAsync(WarningDialogType type, string? symbol = null)
    {
        try
        {
            var page = Application.Current?.MainPage;
            if (page == null)
            {
                _logger.LogError("MainPage is not available - cannot show warning dialog: {Type}", type);
                return false; // Default to cancel if dialog cannot be shown
            }

            var def = GetWarningDefinition(type, symbol);
            var result = await page.DisplayAlert(def.Title, def.Message, def.ConfirmText, def.CancelText);

            _logger.LogInformation("Warning dialog result: {Type} => {Result}",
                type, result ? "Confirmed" : "Cancelled");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing warning dialog: {Type}", type);
            return false; // Default to cancel if dialog fails
        }
    }

    public async Task<bool> ShowExitConfirmationAsync(string message)
    {
        return await ShowWarningAsync(WarningDialogType.ExitAppWithOpenPositions);
    }

    public async Task<bool> ShowReplaceAlgorithmConfirmationAsync(string symbol)
    {
        return await ShowWarningAsync(WarningDialogType.ReplaceAlgoWithOpenPosition, symbol);
    }

    private static WarningDialogDefinition GetWarningDefinition(WarningDialogType type, string? symbol)
    {
        var symbolText = string.IsNullOrWhiteSpace(symbol) ? "this symbol" : symbol;

        return type switch
        {
            WarningDialogType.ExitAppWithOpenPositions => new WarningDialogDefinition(
                "Exit Confirmation",
                "Are you sure you want to exit? The current running algos position will be closed.",
                "Yes",
                "No"),
            WarningDialogType.ReplaceAlgoWithOpenPosition => new WarningDialogDefinition(
                "Replace Running Algorithm",
                $"You are trying to replace running algo of {symbolText} and position will be closed. Are you sure?",
                "Yes",
                "No"),
            WarningDialogType.StopAlgoWithOpenPosition => new WarningDialogDefinition(
                "Stop Algorithm",
                $"Stopping the algorithm will close the open position for {symbolText}. Continue?",
                "Stop",
                "Cancel"),
            WarningDialogType.CloseTileWithOpenPosition => new WarningDialogDefinition(
                "Close Algo Tile",
                $"Closing this tile will close the open position for {symbolText}. Continue?",
                "Close",
                "Cancel"),
            _ => new WarningDialogDefinition(
                "Confirmation",
                "Are you sure?",
                "Yes",
                "No")
        };
    }

    private sealed record WarningDialogDefinition(string Title, string Message, string ConfirmText, string CancelText);
}
