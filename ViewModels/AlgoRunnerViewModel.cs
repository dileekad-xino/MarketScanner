using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketScanner.Models;
using MarketScanner.Services;
using Microsoft.Extensions.Logging;

namespace MarketScanner.ViewModels;

public partial class AlgoRunnerViewModel : ObservableObject
{
    private readonly IAlgoStrategy _algorithm;
    private readonly ILogger<AlgoRunnerViewModel> _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    [ObservableProperty] private ScannerRowViewModel? _selectedSymbol;
    [ObservableProperty] private AlgoResult? _result;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public AlgoRunnerViewModel(
        IAlgoStrategy algorithm,
        ILogger<AlgoRunnerViewModel> logger)
    {
        _algorithm = algorithm;
        _logger = logger;
    }

    public async Task InitializeAsync(ScannerRowViewModel symbol)
    {
        SelectedSymbol = symbol;
        ErrorMessage = string.Empty;
        Result = null;

        _logger.LogInformation("AlgoRunner initialized for symbol {Symbol} with algorithm {AlgorithmName}", 
            symbol.Symbol, _algorithm.Name);
    }

    [RelayCommand]
    private async Task RunAlgoAsync()
    {
        if (SelectedSymbol == null)
        {
            ErrorMessage = "Please select a symbol";
            return;
        }

        try
        {
            IsRunning = true;
            ErrorMessage = string.Empty;
            Result = null;

            // Cancel any previous execution
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("Running algorithm {AlgorithmName} on symbol {Symbol}", 
                _algorithm.Name, SelectedSymbol.Symbol);

            // Execute algorithm
            Result = await _algorithm.ExecuteAsync(SelectedSymbol, _cancellationTokenSource.Token);

            _logger.LogInformation("Algorithm completed: {Action} for {Symbol} at {Price}", 
                Result.Action, Result.Symbol, Result.Price);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Algorithm execution was cancelled");
            ErrorMessage = "Algorithm execution was cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing algorithm");
            ErrorMessage = $"Error executing algorithm: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void CancelExecution()
    {
        _cancellationTokenSource?.Cancel();
        _logger.LogInformation("Algorithm execution cancelled by user");
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        // Cancel any running algorithm
        _cancellationTokenSource?.Cancel();
        
        // Close the page
        if (Application.Current?.MainPage != null)
        {
            await Application.Current.MainPage.Navigation.PopModalAsync();
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }
}

