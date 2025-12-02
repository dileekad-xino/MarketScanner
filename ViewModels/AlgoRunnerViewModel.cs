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
    private readonly IPositionTrackingService _positionTracking;
    private readonly IRsiSettingsService _rsiSettings;
    private readonly ILogger<AlgoRunnerViewModel> _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _monitoringTask;
    private CancellationTokenSource? _monitoringCts;

    [ObservableProperty] private ScannerRowViewModel? _selectedSymbol;
    [ObservableProperty] private AlgoResult? _result;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _errorMessage = string.Empty;
    
    // Position tracking properties
    [ObservableProperty] private bool _isTrackingMode = true; // Default to tracking mode
    [ObservableProperty] private PositionTracker? _currentPosition;
    [ObservableProperty] private ObservableCollection<PositionResult> _completedTrades = new();
    [ObservableProperty] private string _positionStatus = string.Empty;
    [ObservableProperty] private bool _isMonitoring;

    /// <summary>
    /// Whether there are any completed trades to display
    /// </summary>
    public bool HasCompletedTrades => CompletedTrades.Count > 0;

    public AlgoRunnerViewModel(
        IAlgoStrategy algorithm,
        IPositionTrackingService positionTracking,
        IRsiSettingsService rsiSettings,
        ILogger<AlgoRunnerViewModel> logger)
    {
        _algorithm = algorithm;
        _positionTracking = positionTracking;
        _rsiSettings = rsiSettings;
        _logger = logger;
        
        // Subscribe to position closed events
        _positionTracking.PositionClosed += OnPositionClosed;
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

            _logger.LogInformation("Running algorithm {AlgorithmName} on symbol {Symbol} (TrackingMode={TrackingMode})", 
                _algorithm.Name, SelectedSymbol.Symbol, IsTrackingMode);

            // Execute algorithm
            Result = await _algorithm.ExecuteAsync(SelectedSymbol, _cancellationTokenSource.Token);
            if (Result != null && SelectedSymbol != null)
            {
                SelectedSymbol.RsiValue = Result.RsiValue;
                SelectedSymbol.RsiSignal = Result.RsiSignal;
            }

            _logger.LogInformation("Algorithm completed: {Action} for {Symbol} at {Price}", 
                Result.Action, Result.Symbol, Result.Price);

            // If tracking mode is enabled and we got a BUY signal, open a position
            if (IsTrackingMode && Result != null && Result.Action == AlgoAction.Buy)
            {
                await OpenPositionAsync(Result);
            }
            else if (!IsTrackingMode)
            {
                // Immediate mode: just show the result
                PositionStatus = $"Signal: {Result.RsiSignal} | RSI: {Result.RsiValue:F2}";
            }
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

    private async Task OpenPositionAsync(AlgoResult buySignal)
    {
        if (buySignal.RsiValue == null) return;

        var position = new PositionTracker(
            buySignal.Symbol,
            buySignal.RsiSignal ?? "BUY",
            buySignal.Price ?? 0,
            buySignal.RsiValue.Value);

        _positionTracking.OpenPosition(position);
        CurrentPosition = position;
        
        _logger.LogInformation("Position opened: {Symbol} @ ${Price:F2} (RSI={Rsi:F2}) - Signal: {Signal}",
            position.Symbol, position.EntryPrice, position.EntryRsi, position.EntrySignal);

        PositionStatus = $"🟢 Position OPEN: {position.Symbol} @ ${position.EntryPrice:F2} (RSI: {position.EntryRsi:F2})";

        // Start monitoring the position
        await StartMonitoringAsync(position);
    }

    private async Task StartMonitoringAsync(PositionTracker position)
    {
        // Check if monitoring is already running for this position
        // (could be from a previous page instance)
        if (!position.IsOpen)
        {
            _logger.LogInformation("Position {Symbol} is already closed, skipping monitoring", position.Symbol);
            IsMonitoring = false;
            return;
        }

        // Only start new monitoring if not already running for this ViewModel instance
        if (_monitoringTask != null && !_monitoringTask.IsCompleted)
        {
            _logger.LogWarning("Monitoring task already running for this ViewModel instance, skipping");
            return;
        }

        IsMonitoring = true;
        
        // Create a separate cancellation token for monitoring (not tied to algorithm execution)
        // This allows monitoring to continue even if algorithm execution is cancelled
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        _monitoringCts = new CancellationTokenSource();
        _monitoringTask = Task.Run(async () => await MonitorPositionAsync(position, _monitoringCts.Token));
        
        _logger.LogInformation("Started monitoring task for position {Symbol}", position.Symbol);
    }

    private async Task MonitorPositionAsync(PositionTracker position, CancellationToken ct)
    {
        const int checkIntervalSeconds = 5; // Check every 5 seconds
        
        // Get RSI settings for exit conditions
        var settings = await _rsiSettings.GetAsync(ct);
        var oversold = settings.Oversold;
        var overbought = settings.Overbought;
        var takeProfitLevel = settings.TakeProfitLevel;

        _logger.LogInformation("Starting position monitoring for {Symbol} (checking every {Interval}s)",
            position.Symbol, checkIntervalSeconds);

        try
        {
            while (position.IsOpen && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), ct);

                // Get current market data by re-running algorithm
                // Use SelectedSymbol if available, otherwise create a minimal symbol from position
                ScannerRowViewModel? symbolToUse = SelectedSymbol;
                if (symbolToUse == null)
                {
                    // Create a minimal symbol for monitoring (position persists across page navigation)
                    symbolToUse = new ScannerRowViewModel
                    {
                        Symbol = position.Symbol,
                        LastPrice = position.CurrentPrice
                    };
                }

                try
                {
                    var currentResult = await _algorithm.ExecuteAsync(symbolToUse, ct);
                    if (currentResult?.RsiValue == null || currentResult.Price == null)
                    {
                        continue;
                    }

                    var currentPrice = currentResult.Price.Value;
                    var currentRsi = currentResult.RsiValue.Value;

                    // Update position
                    position.Update(currentPrice, currentRsi);

                    // Update UI status (only if this ViewModel is still active)
                    // Use MainThread to safely update UI
                    var unrealizedPnL = position.GetUnrealizedPnLPercent();
                    var duration = position.GetDuration();
                    var statusText = $"🟢 OPEN: {position.Symbol} | Price: ${currentPrice:F2} ({unrealizedPnL:+#0.00;-#0.00;0.00}%) | " +
                                   $"RSI: {currentRsi:F2} | Duration: {duration.TotalMinutes:F1}m | Checks: {position.CheckCount}";
                    
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        // Only update if this ViewModel still has this position as current
                        if (CurrentPosition?.PositionId == position.PositionId)
                        {
                            PositionStatus = statusText;
                        }
                    });

                    // Check for exit conditions
                    var exitSignal = CheckExitConditions(position, currentResult, oversold, overbought, takeProfitLevel);
                    if (exitSignal != null)
                    {
                        _positionTracking.ClosePosition(
                            position.Symbol,
                            exitSignal.Value.signal,
                            currentPrice,
                            currentRsi,
                            exitSignal.Value.reason);

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            // Only update if this ViewModel still has this position as current
                            if (CurrentPosition?.PositionId == position.PositionId)
                            {
                                PositionStatus = $"🔴 CLOSED: {position.Symbol} | Exit: {exitSignal.Value.signal} @ ${currentPrice:F2} | " +
                                               $"PnL: {position.GetRealizedPnLPercent():+#0.00;-#0.00;0.00}%";
                                CurrentPosition = null;
                                IsMonitoring = false;
                            }
                        });
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during position monitoring check for {Symbol}", position.Symbol);
                    // Continue monitoring despite errors
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Position monitoring cancelled for {Symbol}", position.Symbol);
        }
        finally
        {
            IsMonitoring = false;
        }
    }

    private (string signal, string reason)? CheckExitConditions(
        PositionTracker position,
        AlgoResult currentResult,
        double oversold,
        double overbought,
        double takeProfitLevel)
    {
        var currentRsi = currentResult.RsiValue ?? 0;
        var currentSignal = currentResult.RsiSignal ?? "";

        // Exit conditions based on RSI levels and signals
        // 1. STRONG SELL signal (overbought rejection or absolute)
        if (currentResult.Action == AlgoAction.Sell && currentSignal.Contains("STRONG SELL"))
        {
            return ("STRONG SELL", currentResult.Reason ?? "Overbought exit");
        }

        // 2. SELL signal (50-cross down or 60/70 rejection)
        if (currentResult.Action == AlgoAction.Sell && currentSignal.Contains("SELL"))
        {
            return ("SELL", currentResult.Reason ?? "Sell signal exit");
        }

        // 3. RSI crosses back below entry RSI (stop-loss like)
        if (currentRsi < position.EntryRsi - 5) // 5 RSI points below entry
        {
            return ("STOP LOSS", $"RSI dropped to {currentRsi:F2} (below entry {position.EntryRsi:F2})");
        }

        // 4. For 50-cross entries: check 60/70 rejection (handled by strategy)
        // This is already handled by the strategy's swing exit logic

        return null; // No exit condition met
    }

    private void OnPositionClosed(object? sender, PositionResult result)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CompletedTrades.Insert(0, result); // Add to top of list
            OnPropertyChanged(nameof(HasCompletedTrades)); // Notify UI
            _logger.LogInformation("Position closed: {Summary}", result.GetSummary());
        });
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
        // Cancel algorithm execution
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        
        // Note: We DON'T cancel monitoring here - let it continue in background
        // Monitoring will stop naturally when position closes or app closes
        // _monitoringCts?.Cancel(); // Commented out to allow background monitoring
        
        _positionTracking.PositionClosed -= OnPositionClosed;
    }
}

