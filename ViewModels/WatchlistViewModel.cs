using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using MarketScanner.Views.Dialogs;
using Microsoft.Extensions.Logging;
using System.Reactive.Linq;

namespace MarketScanner.ViewModels;

public partial class WatchlistViewModel : ObservableObject, IDisposable
{
    private readonly IWatchlistService _watchlistService;
    private readonly IbkrGatewayService _ibkrService;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<WatchlistViewModel> _logger;
    private readonly ISymbolSearchService? _symbolSearchService;

    [ObservableProperty] private ObservableCollection<Watchlist> _watchlists = new();
    [ObservableProperty] private Watchlist? _selectedWatchlist;
    [ObservableProperty] private ObservableCollection<ScannerRowViewModel> _watchlistItems = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = string.Empty;
    
    // Inline watchlist creation popup
    [ObservableProperty] private bool _isCreatingWatchlist = false;
    [ObservableProperty] private string _newWatchlistName = "";
    [ObservableProperty] private string _watchlistNameError = "";
    
    // Inline watchlist rename popup
    [ObservableProperty] private bool _isRenamingWatchlist = false;
    [ObservableProperty] private int? _renamingWatchlistId = null;
    [ObservableProperty] private string _renamingWatchlistName = "";
    [ObservableProperty] private string _renamingWatchlistError = "";
    
    // Add symbol input
    [ObservableProperty] private string _newSymbolText = "";
    [ObservableProperty] private ObservableCollection<SymbolSearchResult> _searchResults = new();
    [ObservableProperty] private bool _showSearchResults = false;
    [ObservableProperty] private bool _isSearching = false;

    private readonly Dictionary<string, ScannerRowViewModel> _rowCache = new();
    private readonly ConcurrentQueue<TickData> _batchedTicks = new();
    private readonly System.Timers.Timer _batchTimer;
    private IDisposable? _tickSubscription;
    private IDisposable? _playbackSubscription;
    private MarketScanner.Services.Impl.DelayedNdjsonTickSource? _playbackSource;
    private bool _disposed = false;

    private const int BatchIntervalMs = 16; // ~60 FPS for smooth updates
    private const int MaxBatchSize = 50;
    private readonly TimeSpan _searchDebounceDelay = TimeSpan.FromMilliseconds(300);
    private CancellationTokenSource? _searchCts;

    public WatchlistViewModel(
        IWatchlistService watchlistService,
        IbkrGatewayService ibkrService,
        IDispatcherService dispatcher,
        ILogger<WatchlistViewModel> logger,
        ISymbolSearchService? symbolSearchService = null)
    {
        _watchlistService = watchlistService;
        _ibkrService = ibkrService;
        _dispatcher = dispatcher;
        _logger = logger;
        _symbolSearchService = symbolSearchService;

        // Setup batch timer for smooth updates (60 FPS)
        _batchTimer = new System.Timers.Timer(BatchIntervalMs);
        _batchTimer.Elapsed += (_, _) => FlushBatchedTicks();
        _batchTimer.AutoReset = true;
        _batchTimer.Start();

        // Subscribe to tick updates (IBKR)
        _tickSubscription = _ibkrService.TickStream.Subscribe(tick =>
        {
            _batchedTicks.Enqueue(tick);
        });

        // Fallback playback stream (NDJSON or synthetic via controller)
        var fb = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
        if (fb != null && fb.IsActive)
        {
            _playbackSubscription = fb.TickStream.Subscribe(t => _batchedTicks.Enqueue(t));
        }
        else
        {
            // Legacy NDJSON env-based attach (best-effort)
            _playbackSource = MarketScanner.Services.Impl.DelayedNdjsonTickSource.CreateFromEnv();
            if (_playbackSource != null)
            {
                _playbackSource.Start();
                _playbackSubscription = _playbackSource.Stream.Subscribe(tick => _batchedTicks.Enqueue(tick));
            }
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;

            await _watchlistService.InitializeAsync();
            var watchlists = await _watchlistService.GetAllWatchlistsAsync();

            Watchlists.Clear();
            foreach (var w in watchlists)
            {
                Watchlists.Add(w);
            }

            // Select last (most recent) watchlist if available
            if (Watchlists.Count > 0)
            {
                SelectedWatchlist = Watchlists[Watchlists.Count - 1];
            }

            _logger.LogInformation("Initialized watchlist window with {Count} watchlists", Watchlists.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize watchlist window");
            ErrorMessage = $"Failed to load watchlists: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedWatchlistChanged(Watchlist? value)
    {
        if (value != null)
        {
            _ = LoadWatchlistItemsAsync(value.Id);
        }
        // Notify that CanAddSymbol changed
        OnPropertyChanged(nameof(CanAddSymbol));
    }

    // Computed property to enable/disable add symbol button
    public bool CanAddSymbol => SelectedWatchlist != null;

    private async Task LoadWatchlistItemsAsync(int watchlistId)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;

            // Clear existing items
            _rowCache.Clear();
            WatchlistItems.Clear();

            // Load items from database
            var items = await _watchlistService.GetWatchlistItemsAsync(watchlistId);
            _logger.LogInformation("Loading {Count} items for watchlist {WatchlistId}", items.Count, watchlistId);

            foreach (var item in items)
            {
                // Create ViewModel for this symbol
                var rowVm = new ScannerRowViewModel(_logger)
                {
                    Symbol = item.Symbol,
                    Company = item.Company ?? item.Symbol
                };
                _rowCache[item.Symbol] = rowVm;
                WatchlistItems.Add(rowVm);
            }

            _logger.LogInformation("Loaded {Count} items into watchlist view. Market data will arrive via tick stream.", WatchlistItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load watchlist items");
            ErrorMessage = $"Failed to load watchlist: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void FlushBatchedTicks()
    {
        if (_batchedTicks.Count == 0 || _disposed)
            return;

        try
        {
            _dispatcher.OnUI(() =>
            {
                var processed = 0;
                while (processed < MaxBatchSize && _batchedTicks.TryDequeue(out var tick))
                {
                    if (_rowCache.TryGetValue(tick.Symbol, out var row))
                    {
                        tick.ApplyTo(row);  // In-place update using the TickData extension method
                    }
                    processed++;
                }
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Unable to find main thread"))
        {
            // UI not ready yet or app shutting down - just skip this batch
            // This can happen during app initialization or shutdown when the main thread is unavailable
            _logger.LogDebug("Skipping tick flush - main thread not available (app may be shutting down)");
        }
    }

    [RelayCommand]
    private async Task CreateWatchlistAsync()
    {
        try
        {
            // Get base name and suggest next available name
            var baseName = "Market Scanner";
            var existingNames = Watchlists.Select(w => w.Name).ToHashSet();
            var number = 1;
            string suggestedName;
            
            do
            {
                suggestedName = $"{baseName} {number}";
                number++;
            } while (existingNames.Contains(suggestedName));

            // Set suggested name and show inline popup
            NewWatchlistName = suggestedName;
            IsCreatingWatchlist = true;
            
            await Task.CompletedTask; // Keep async signature
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show watchlist creation popup");
            ErrorMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ConfirmCreateWatchlistAsync()
    {
        try
        {
            // Validate name
            var trimmedName = NewWatchlistName?.Trim() ?? "";
            
            // Validation: Empty or whitespace
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                WatchlistNameError = "Name cannot be empty";
                _logger.LogDebug("Watchlist creation failed - empty name");
                return;
            }
            
            // Validation: Length (max 100 chars per database schema)
            if (trimmedName.Length > 100)
            {
                WatchlistNameError = "Name too long (max 100 characters)";
                _logger.LogDebug("Watchlist creation failed - name too long");
                return;
            }
            
            // Validation: Duplicate name
            if (Watchlists.Any(w => w.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase)))
            {
                WatchlistNameError = "Name already exists";
                _logger.LogDebug("Watchlist creation failed - duplicate name");
                return;
            }
            
            // Validation: Invalid characters (prevent special chars that might break UI/DB)
            var invalidChars = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            if (trimmedName.Any(c => invalidChars.Contains(c)))
            {
                WatchlistNameError = "Name contains invalid characters";
                _logger.LogDebug("Watchlist creation failed - invalid characters");
                return;
            }

            // All validations passed - create watchlist
            var newWatchlist = await _watchlistService.CreateWatchlistAsync(trimmedName);
            Watchlists.Add(newWatchlist);
            SelectedWatchlist = newWatchlist;

            _logger.LogInformation("Created empty watchlist '{Name}'", trimmedName);
            
            // Hide popup and clear state
            IsCreatingWatchlist = false;
            NewWatchlistName = "";
            WatchlistNameError = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create watchlist");
            WatchlistNameError = "Failed to create watchlist";
        }
    }

    [RelayCommand]
    private void CancelCreateWatchlist()
    {
        IsCreatingWatchlist = false;
        NewWatchlistName = "";
        WatchlistNameError = "";
        _logger.LogInformation("Watchlist creation cancelled");
    }

    partial void OnNewWatchlistNameChanged(string value)
    {
        // Clear error when user starts typing
        WatchlistNameError = "";
    }

    [RelayCommand]
    private void SelectWatchlist(Watchlist watchlist)
    {
        SelectedWatchlist = watchlist;
    }

    [RelayCommand]
    private async Task RenameWatchlistAsync(Watchlist watchlist)
    {
        try
        {
            // Set up rename popup with current name pre-filled
            RenamingWatchlistId = watchlist.Id;
            RenamingWatchlistName = watchlist.Name;
            RenamingWatchlistError = "";
            IsRenamingWatchlist = true;
            
            await Task.CompletedTask; // Keep async signature
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show watchlist rename popup");
            RenamingWatchlistError = $"Error: {ex.Message}";
        }
    }
    
    [RelayCommand]
    private async Task ConfirmRenameWatchlistAsync()
    {
        try
        {
            if (!RenamingWatchlistId.HasValue)
            {
                RenamingWatchlistError = "No watchlist selected for rename";
                return;
            }
            
            // Validate name
            var trimmedName = RenamingWatchlistName?.Trim() ?? "";
            
            // Validation: Empty or whitespace
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                RenamingWatchlistError = "Name cannot be empty";
                _logger.LogDebug("Watchlist rename failed - empty name");
                return;
            }
            
            // Validation: Length (max 100 chars per database schema)
            if (trimmedName.Length > 100)
            {
                RenamingWatchlistError = "Name too long (max 100 characters)";
                _logger.LogDebug("Watchlist rename failed - name too long");
                return;
            }
            
            // Validation: Duplicate name (excluding current watchlist)
            if (Watchlists.Any(w => w.Id != RenamingWatchlistId.Value && w.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase)))
            {
                RenamingWatchlistError = "Name already exists";
                _logger.LogDebug("Watchlist rename failed - duplicate name");
                return;
            }
            
            // Validation: Invalid characters (prevent special chars that might break UI/DB)
            var invalidChars = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            if (trimmedName.Any(c => invalidChars.Contains(c)))
            {
                RenamingWatchlistError = "Name contains invalid characters";
                _logger.LogDebug("Watchlist rename failed - invalid characters");
                return;
            }

            // All validations passed - rename watchlist
            var success = await _watchlistService.RenameWatchlistAsync(RenamingWatchlistId.Value, trimmedName);
            if (!success)
            {
                RenamingWatchlistError = "Failed to rename watchlist";
                _logger.LogWarning("RenameWatchlistAsync returned false for watchlist {Id}", RenamingWatchlistId.Value);
                return;
            }
            
            // Update watchlist in collection
            var watchlist = Watchlists.FirstOrDefault(w => w.Id == RenamingWatchlistId.Value);
            if (watchlist != null)
            {
                var wasSelected = SelectedWatchlist?.Id == watchlist.Id;
                
                // Update properties
                watchlist.Name = trimmedName;
                watchlist.UpdatedAt = DateTime.UtcNow;
                
                // Trigger collection update by removing and re-adding (ensures UI refresh)
                var index = Watchlists.IndexOf(watchlist);
                Watchlists.RemoveAt(index);
                Watchlists.Insert(index, watchlist);
                
                // Update SelectedWatchlist reference to ensure UI updates
                if (wasSelected)
                {
                    SelectedWatchlist = watchlist;
                }
            }

            _logger.LogInformation("Renamed watchlist {Id} to '{Name}'", RenamingWatchlistId.Value, trimmedName);
            
            // Hide popup and clear state
            IsRenamingWatchlist = false;
            RenamingWatchlistId = null;
            RenamingWatchlistName = "";
            RenamingWatchlistError = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename watchlist");
            RenamingWatchlistError = "Failed to rename watchlist";
        }
    }
    
    [RelayCommand]
    private void CancelRenameWatchlist()
    {
        IsRenamingWatchlist = false;
        RenamingWatchlistId = null;
        RenamingWatchlistName = "";
        RenamingWatchlistError = "";
        _logger.LogInformation("Watchlist rename cancelled");
    }
    
    partial void OnRenamingWatchlistNameChanged(string value)
    {
        // Clear error when user starts typing
        RenamingWatchlistError = "";
    }

    partial void OnNewSymbolTextChanged(string value)
    {
        _logger.LogInformation("WatchlistViewModel.OnNewSymbolTextChanged called with value: '{Value}' (length: {Length})", value ?? "(null)", value?.Length ?? 0);
        System.Diagnostics.Debug.WriteLine($"WatchlistViewModel.OnNewSymbolTextChanged: value='{value}', length={value?.Length ?? 0}");
        
        // Clear error when user starts typing
        ErrorMessage = "";
        
        // Trigger search if 2+ characters
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
        {
            _logger.LogDebug("WatchlistViewModel: Search not triggered - value too short or empty");
            ShowSearchResults = false;
            SearchResults.Clear();
            return;
        }
        
        _logger.LogInformation("WatchlistViewModel: Triggering search for query: '{Query}'", value);
        System.Diagnostics.Debug.WriteLine($"WatchlistViewModel: Starting PerformSearchAsync for '{value}'");
        _ = PerformSearchAsync(value);
    }
    
    private async Task PerformSearchAsync(string query)
    {
        _logger.LogInformation("WatchlistViewModel.PerformSearchAsync started for query: '{Query}'", query);
        System.Diagnostics.Debug.WriteLine($"WatchlistViewModel.PerformSearchAsync: Started for query '{query}'");
        
        // Cancel previous search
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        
        try
        {
            // Debounce
            _logger.LogDebug("WatchlistViewModel: Waiting for debounce delay: {Delay}ms", _searchDebounceDelay.TotalMilliseconds);
            await Task.Delay(_searchDebounceDelay, _searchCts.Token);
            
            if (_symbolSearchService == null)
            {
                _logger.LogWarning("WatchlistViewModel: Symbol search service is null - search cannot proceed");
                System.Diagnostics.Debug.WriteLine("WatchlistViewModel: _symbolSearchService is NULL!");
                return;
            }
            
            _logger.LogInformation("WatchlistViewModel: _symbolSearchService is not null, proceeding with search");
            System.Diagnostics.Debug.WriteLine($"WatchlistViewModel: _symbolSearchService is available, type: {_symbolSearchService.GetType().Name}");
            
            if (_searchCts.Token.IsCancellationRequested)
            {
                _logger.LogDebug("WatchlistViewModel: Search was cancelled during debounce");
                return;
            }
                
            _logger.LogInformation("WatchlistViewModel: Executing search for query: '{Query}'", query);
            System.Diagnostics.Debug.WriteLine($"WatchlistViewModel: Calling _symbolSearchService.SearchSymbolsAsync('{query}')");
            IsSearching = true;
            var results = await _symbolSearchService.SearchSymbolsAsync(query, _searchCts.Token);
            
            _logger.LogInformation("WatchlistViewModel: Search completed - received {Count} results for query: '{Query}'", results.Count, query);
            System.Diagnostics.Debug.WriteLine($"WatchlistViewModel: Search returned {results.Count} results");
            
            if (!_searchCts.Token.IsCancellationRequested)
            {
                _logger.LogInformation("WatchlistViewModel: Updating UI on UI thread with {Count} results", results.Count);
                await _dispatcher.OnUIAsync(() =>
                {
                    _logger.LogInformation("WatchlistViewModel: On UI thread - clearing and adding {Count} results", results.Count);
                    SearchResults.Clear();
                    foreach (var result in results)
                    {
                        SearchResults.Add(result);
                        _logger.LogDebug("WatchlistViewModel: Added result: {Symbol}", result.Symbol);
                    }
                    ShowSearchResults = results.Count > 0;
                    _logger.LogInformation("WatchlistViewModel: Updated UI - SearchResults.Count={Count}, ShowSearchResults={Show}", SearchResults.Count, ShowSearchResults);
                    System.Diagnostics.Debug.WriteLine($"WatchlistViewModel: UI updated - SearchResults.Count={SearchResults.Count}, ShowSearchResults={ShowSearchResults}");
                });
            }
            else
            {
                _logger.LogDebug("WatchlistViewModel: Search was cancelled before UI update");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("WatchlistViewModel: Search was cancelled (expected when user types again)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WatchlistViewModel: Symbol search failed for query: '{Query}'", query);
            System.Diagnostics.Debug.WriteLine($"WatchlistViewModel: Exception in PerformSearchAsync: {ex.Message}");
        }
        finally
        {
            IsSearching = false;
            _logger.LogDebug("WatchlistViewModel: PerformSearchAsync completed for query: '{Query}'", query);
        }
    }
    
    partial void OnShowSearchResultsChanged(bool value)
    {
        _logger.LogInformation("WatchlistViewModel: ShowSearchResults changed to: {Value}", value);
        System.Diagnostics.Debug.WriteLine($"WatchlistViewModel: ShowSearchResults changed to {value}");
    }

    [RelayCommand]
    private void SelectSearchResult(SymbolSearchResult result)
    {
        NewSymbolText = result.Symbol;
        ShowSearchResults = false;
        SearchResults.Clear();
    }

    [RelayCommand]
    private async Task DeleteWatchlistAsync(Watchlist watchlist)
    {
        try
        {
            // Show confirmation dialog
            if (Application.Current?.MainPage == null)
            {
                _logger.LogWarning("Cannot show confirmation dialog - MainPage is null");
                return;
            }

            bool confirmed = await Application.Current.MainPage.DisplayAlert(
                "Delete Watchlist",
                $"Are you sure you want to delete '{watchlist.Name}'?",
                "Delete",
                "Cancel");

            if (!confirmed)
            {
                _logger.LogDebug("User cancelled deletion of watchlist '{Name}'", watchlist.Name);
                return;
            }

            await _watchlistService.DeleteWatchlistAsync(watchlist.Id);
            Watchlists.Remove(watchlist);

            if (SelectedWatchlist?.Id == watchlist.Id)
            {
                SelectedWatchlist = Watchlists.FirstOrDefault();
            }

            _logger.LogInformation("Deleted watchlist '{Name}'", watchlist.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete watchlist");
            ErrorMessage = $"Failed to delete watchlist: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RemoveFromWatchlistAsync(ScannerRowViewModel row)
    {
        if (SelectedWatchlist == null)
            return;

        try
        {
            await _watchlistService.RemoveItemAsync(SelectedWatchlist.Id, row.Symbol);
            WatchlistItems.Remove(row);
            _rowCache.Remove(row.Symbol);

            _logger.LogInformation("Removed {Symbol} from watchlist '{Name}'", row.Symbol, SelectedWatchlist.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove symbol from watchlist");
            ErrorMessage = $"Failed to remove symbol: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddSymbolAsync()
    {
        try
        {
            // Validate that a watchlist is selected (button will be disabled if null)
            if (SelectedWatchlist == null)
            {
                return;
            }

            var symbol = NewSymbolText?.Trim().ToUpperInvariant() ?? "";

            if (string.IsNullOrWhiteSpace(symbol))
            {
                ErrorMessage = "Please enter a symbol";
                return;
            }

            // Check if symbol already exists in watchlist (silently skip if duplicate)
            if (_rowCache.ContainsKey(symbol))
            {
                NewSymbolText = "";
                _logger.LogDebug("Symbol {Symbol} already exists in watchlist, skipping", symbol);
                return;
            }

            ErrorMessage = "";

            // Add symbol to watchlist service (use symbol as company name)
            await _watchlistService.AddItemsAsync(SelectedWatchlist.Id, new List<(string Symbol, string Company)> { (symbol, symbol) });

            // Subscribe to market data for this symbol (ensures live updates work)
            // Only subscribe via IBKR if connected, otherwise rely on fallback playback
            try
            {
                _ibkrService.SubscribeToSymbols(new[] { symbol });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not subscribe to IBKR market data for {Symbol}, will use fallback if available", symbol);
            }

            // Update fallback playback with new symbol if active
            var fallback = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
            if (fallback != null && fallback.IsActive)
            {
                // Get current symbols and add the new one
                var currentSymbols = fallback.CurrentSymbols.ToList();
                if (!currentSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                {
                    currentSymbols.Add(symbol);
                    fallback.UpdateSymbols(currentSymbols);
                }
            }

            // Create ViewModel for this symbol
            var rowVm = new ScannerRowViewModel(_logger)
            {
                Symbol = symbol,
                Company = symbol
            };

            _rowCache[symbol] = rowVm;
            WatchlistItems.Add(rowVm);

            NewSymbolText = "";
            _logger.LogInformation("Added symbol {Symbol} to watchlist '{Name}' and subscribed to market data", symbol, SelectedWatchlist.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add symbol to watchlist");
            ErrorMessage = $"Failed to add symbol: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _disposed = true;

        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
        }
        catch { }

        _batchTimer?.Stop();
        _batchTimer?.Dispose();

        _tickSubscription?.Dispose();
        _playbackSubscription?.Dispose();
        _playbackSource?.Dispose();

        _rowCache.Clear();
    }
}

