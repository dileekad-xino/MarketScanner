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

    public async Task<bool> ShowExitConfirmationAsync(string message)
    {
        try
        {
            var page = Application.Current?.MainPage;
            if (page == null)
            {
                _logger.LogError("MainPage is not available - cannot show exit confirmation dialog");
                return false; // Default to cancel if dialog cannot be shown
            }

            var result = await page.DisplayAlert(
                "Exit Confirmation",
                message,
                "Yes",
                "No");

            _logger.LogInformation("Exit confirmation dialog result: {Result}", result ? "Confirmed" : "Cancelled");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing exit confirmation dialog");
            return false; // Default to cancel if dialog fails
        }
    }

    public async Task<bool> ShowReplaceAlgorithmConfirmationAsync(string symbol)
    {
        try
        {
            var page = Application.Current?.MainPage;
            if (page == null)
            {
                _logger.LogError("MainPage is not available - cannot show replace confirmation dialog");
                return false; // Default to cancel if dialog cannot be shown
            }

            var result = await page.DisplayAlert(
                "Replace Running Algorithm",
                $"You are trying to replace running algo of {symbol} and position will be closed. Are you sure?",
                "Yes",
                "No");

            _logger.LogInformation("Replace algorithm confirmation dialog result for {Symbol}: {Result}", 
                symbol, result ? "Confirmed" : "Cancelled");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing replace algorithm confirmation dialog for {Symbol}", symbol);
            return false; // Default to cancel if dialog fails
        }
    }
}
