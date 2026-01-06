using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;

namespace MarketScanner.Services;

public class AlgoRunnerManagerService : ObservableObject
{
    private readonly ILogger<AlgoRunnerManagerService> _logger;
    private readonly List<AlgoRunnerViewModel> _algoRunners = new();
    private const int MaxAlgoRunners = 3;

    public AlgoRunnerManagerService(ILogger<AlgoRunnerManagerService> logger)
    {
        _logger = logger;
    }

    public ObservableCollection<AlgoRunnerViewModel> AlgoRunners { get; } = new();

    public int Count => AlgoRunners.Count;

    public bool CanAdd => AlgoRunners.Count < MaxAlgoRunners;

    public async Task<bool> AddAlgoRunnerAsync(AlgoRunnerViewModel algoRunner)
    {
        if (algoRunner == null)
        {
            _logger.LogWarning("Attempted to add null AlgoRunnerViewModel");
            return false;
        }

        // If we have space, just add it
        if (AlgoRunners.Count < MaxAlgoRunners)
        {
            AlgoRunners.Add(algoRunner);
            _logger.LogInformation("Added AlgoRunner for {Symbol}. Total: {Count}", 
                algoRunner.SelectedSymbol?.Symbol ?? "Unknown", AlgoRunners.Count);
            return true;
        }

        // We're at max capacity - need user to choose which to replace
        var result = await ShowReplaceDialogAsync();
        if (result == null)
        {
            _logger.LogInformation("User cancelled replacing AlgoRunner");
            return false;
        }

        // Replace the selected one
        var index = AlgoRunners.IndexOf(result);
        if (index >= 0)
        {
            // Dispose the old one
            result.Dispose();
            AlgoRunners[index] = algoRunner;
            _logger.LogInformation("Replaced AlgoRunner at index {Index} for {Symbol}", 
                index, algoRunner.SelectedSymbol?.Symbol ?? "Unknown");
            return true;
        }

        _logger.LogWarning("Selected AlgoRunner not found in collection");
        return false;
    }

    public bool RemoveAlgoRunner(AlgoRunnerViewModel algoRunner)
    {
        if (algoRunner == null)
        {
            _logger.LogWarning("Attempted to remove null AlgoRunnerViewModel");
            return false;
        }

        if (AlgoRunners.Remove(algoRunner))
        {
            algoRunner.Dispose();
            _logger.LogInformation("Removed AlgoRunner for {Symbol}. Total: {Count}", 
                algoRunner.SelectedSymbol?.Symbol ?? "Unknown", AlgoRunners.Count);
            return true;
        }

        _logger.LogWarning("AlgoRunner not found in collection");
        return false;
    }

    public void RemoveAlgoRunnerAt(int index)
    {
        if (index >= 0 && index < AlgoRunners.Count)
        {
            var algoRunner = AlgoRunners[index];
            AlgoRunners.RemoveAt(index);
            algoRunner.Dispose();
            _logger.LogInformation("Removed AlgoRunner at index {Index} for {Symbol}. Total: {Count}", 
                index, algoRunner.SelectedSymbol?.Symbol ?? "Unknown", AlgoRunners.Count);
        }
    }

    public void Clear()
    {
        foreach (var algoRunner in AlgoRunners)
        {
            algoRunner.Dispose();
        }
        AlgoRunners.Clear();
        _logger.LogInformation("Cleared all AlgoRunners");
    }

    private async Task<AlgoRunnerViewModel?> ShowReplaceDialogAsync()
    {
        if (AlgoRunners.Count == 0)
            return null;

        // Create a simple selection dialog
        var page = Application.Current?.MainPage;
        if (page == null)
        {
            _logger.LogError("MainPage is not available - cannot show replace dialog");
            return null;
        }

        var options = AlgoRunners.Select((ar, idx) => 
            $"Algo {idx + 1} - {ar.SelectedSymbol?.Symbol ?? "Unknown"}").ToArray();

        var action = await page.DisplayActionSheet(
            "Maximum of 3 Algo Runners. Choose one to replace:",
            "Cancel",
            null,
            options);

        if (string.IsNullOrEmpty(action) || action == "Cancel")
            return null;

        // Find the selected index
        for (int i = 0; i < options.Length; i++)
        {
            if (options[i] == action)
            {
                return AlgoRunners[i];
            }
        }

        return null;
    }
}

