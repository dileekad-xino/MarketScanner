using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using Microsoft.Extensions.Logging;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace MarketScanner.ViewModels;

public partial class QuoteViewModel : ObservableObject, IDisposable
{
    private readonly IbkrGatewayService _ibkrService;
    private readonly IDispatcherService _dispatcher;
    private readonly IWatchlistService _watchlistService;
    private readonly ILogger<QuoteViewModel> _logger;
    private readonly ISymbolSearchService? _symbolSearchService;

    [ObservableProperty] private ObservableCollection<ScannerRowViewModel> _quoteItems = new();
    [ObservableProperty] private string _newSymbolText = "";
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ObservableCollection<Watchlist> _watchlists = new();
    [ObservableProperty] private Watchlist? _selectedWatchlist;
    [ObservableProperty] private ObservableCollection<SymbolSearchResult> _searchResults = new();
    [ObservableProperty] private bool _showSearchResults = false;
    [ObservableProperty] private bool _isSearching = false;
    [ObservableProperty] private int _selectedSearchResultIndex = -1;
    private Watchlist? _previousWatchlist; // Track previous selection to detect Scanner -> Watchlist transitions
    private bool _isSyncing = false; // Flag to prevent restore when syncing from scanner refresh

    // Computed property to enable/disable watchlist picker
    public bool HasWatchlists => Watchlists.Count > 0;

    private readonly Dictionary<string, ScannerRowViewModel> _rowCache = new();
    private readonly ConcurrentQueue<TickData> _batchedTicks = new();
    private readonly System.Timers.Timer _batchTimer;
    private IDisposable? _tickSubscription;
    private IDisposable? _playbackSubscription;
    private MarketScanner.Services.Impl.DelayedNdjsonTickSource? _playbackSource;
    private bool _disposed = false;

    // Snapshot for restoring quotes when switching back from watchlist
    private List<ScannerRowViewModel>? _savedQuoteItems;
    private Dictionary<string, ScannerRowViewModel>? _savedRowCache;

    private const int BatchIntervalMs = 16; // ~60 FPS for smooth updates
    private const int MaxBatchSize = 50;
    private readonly TimeSpan _searchDebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly IServiceProvider? _serviceProvider;
    private CancellationTokenSource? _searchCts;

    public QuoteViewModel(
        IbkrGatewayService ibkrService,
        IDispatcherService dispatcher,
        IWatchlistService watchlistService,
        ILogger<QuoteViewModel> logger,
        IServiceProvider? serviceProvider = null,
        ISymbolSearchService? symbolSearchService = null)
    {
        _ibkrService = ibkrService;
        _dispatcher = dispatcher;
        _watchlistService = watchlistService;
        _logger = logger;
        _serviceProvider = serviceProvider;
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
        _logger.LogInformation("Quote panel initialized");
        await LoadWatchlistsAsync();
    }

    /// <summary>
    /// Resumes subscriptions to all symbols in QuoteItems when switching back to Quote view.
    /// This ensures live updates continue after scanner cancels subscriptions.
    /// </summary>
    public async Task ResumeSubscriptionsAsync()
    {
        if (QuoteItems.Count == 0)
        {
            _logger.LogDebug("QuoteViewModel: No symbols to resume subscriptions for");
            return;
        }

        var symbols = QuoteItems.Select(item => item.Symbol).ToList();
        _logger.LogInformation("QuoteViewModel: Resuming subscriptions for {Count} symbols: {Symbols}", 
            symbols.Count, string.Join(", ", symbols));

        try
        {
            _ibkrService.SubscribeToSymbols(symbols);
            _logger.LogInformation("QuoteViewModel: Successfully resumed subscriptions for {Count} symbols", symbols.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuoteViewModel: Failed to resume subscriptions for symbols");
        }

        await Task.CompletedTask;
    }

    private async Task LoadWatchlistsAsync()
    {
        try
        {
            await _watchlistService.InitializeAsync();
            var watchlists = await _watchlistService.GetAllWatchlistsAsync();

            Watchlists.Clear();
            
            // Add "Scanner" placeholder as first option to restore saved quotes
            var scannerWatchlist = new Watchlist
            {
                Id = -1,
                Name = "Scanner",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Watchlists.Add(scannerWatchlist);
            
            foreach (var w in watchlists)
            {
                Watchlists.Add(w);
            }

            // Notify that HasWatchlists changed
            OnPropertyChanged(nameof(HasWatchlists));

            _logger.LogInformation("Loaded {Count} watchlists for quote view (including Scanner option)", Watchlists.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load watchlists");
            ErrorMessage = $"Failed to load watchlists: {ex.Message}";
        }
    }

    partial void OnSelectedWatchlistChanged(Watchlist? value)
    {
        if (value == null)
        {
            _previousWatchlist = null;
            return;
        }

        // Handle "Scanner" option (Id = -1) - restore saved snapshot
        if (value.Id == -1)
        {
            _previousWatchlist = value;
            // Skip restore if we're syncing from scanner refresh (to prevent duplicates)
            if (!_isSyncing)
            {
                RestoreSavedQuotes();
            }
            return;
        }

        // Always save snapshot when switching FROM "Scanner" (Id=-1 or null) TO a watchlist
        // This preserves the current scanner quotes (including newly added ones) when switching back
        var wasOnScanner = _previousWatchlist == null || _previousWatchlist.Id == -1;
        var shouldSaveSnapshot = wasOnScanner && QuoteItems.Count > 0;

        // Load watchlist (will save snapshot if coming from Scanner)
        _ = LoadQuotesFromWatchlistAsync(value.Id, shouldSaveSnapshot);
        
        _previousWatchlist = value;
    }

    private void RestoreSavedQuotes()
    {
        try
        {
            if (_savedQuoteItems == null || _savedRowCache == null)
            {
                return;
            }

            // Get symbols to re-subscribe
            var symbolsToSubscribe = _savedQuoteItems.Select(q => q.Symbol).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

            // Clear current quotes
            QuoteItems.Clear();
            _rowCache.Clear();

            // Restore saved quotes
            foreach (var item in _savedQuoteItems)
            {
                QuoteItems.Add(item);
                _rowCache[item.Symbol] = item;
            }

            // Restore row cache (in case there are additional entries)
            foreach (var kvp in _savedRowCache)
            {
                if (!_rowCache.ContainsKey(kvp.Key))
                {
                    _rowCache[kvp.Key] = kvp.Value;
                }
            }

            // Re-subscribe to market data for restored symbols
            if (symbolsToSubscribe.Count > 0)
            {
                try
                {
                    _ibkrService.SubscribeToSymbols(symbolsToSubscribe);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not re-subscribe to IBKR market data for restored quotes, will use fallback if available");
                }

                // Update fallback playback with symbols if active
                var fallback = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
                if (fallback != null && fallback.IsActive)
                {
                    var currentSymbols = fallback.CurrentSymbols.ToList();
                    foreach (var symbol in symbolsToSubscribe)
                    {
                        if (!currentSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                        {
                            currentSymbols.Add(symbol);
                        }
                    }
                    fallback.UpdateSymbols(currentSymbols);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore saved quotes");
            ErrorMessage = $"Failed to restore quotes: {ex.Message}";
        }
    }

    private async Task LoadQuotesFromWatchlistAsync(int watchlistId, bool saveSnapshot = false)
    {
        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;

            // Only save snapshot when switching FROM "Scanner" TO a watchlist
            // This preserves the original scanner quotes when switching back
            if (saveSnapshot && QuoteItems.Count > 0)
            {
                _savedQuoteItems = new List<ScannerRowViewModel>(QuoteItems);
                _savedRowCache = new Dictionary<string, ScannerRowViewModel>(_rowCache);
                _logger.LogDebug("Saved {Count} quotes to snapshot before loading watchlist (switching from Scanner)", _savedQuoteItems.Count);
            }
            else if (_savedQuoteItems == null && QuoteItems.Count == 0)
            {
                // No current quotes and no saved snapshot - initialize empty snapshot
                _savedQuoteItems = new List<ScannerRowViewModel>();
                _savedRowCache = new Dictionary<string, ScannerRowViewModel>();
            }

            // Get watchlist items
            var items = await _watchlistService.GetWatchlistItemsAsync(watchlistId);
            _logger.LogInformation("Loading {Count} symbols from watchlist {WatchlistId} into quotes", items.Count, watchlistId);

            // Get symbols to subscribe
            var symbolsToSubscribe = new List<string>();

            // Clear existing quotes and add watchlist symbols
            QuoteItems.Clear();
            _rowCache.Clear();

            foreach (var item in items)
            {
                var symbol = item.Symbol?.Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(symbol)) continue;

                // Create ViewModel for this symbol
                var rowVm = new ScannerRowViewModel(_logger)
                {
                    Symbol = symbol,
                    Company = item.Company ?? symbol,
                    Region = "United States",
                    Product = "Stocks",
                    Exchange = "us stocks",
                    IsDropped = false
                };

                _rowCache[symbol] = rowVm;
                QuoteItems.Add(rowVm);
                symbolsToSubscribe.Add(symbol);
            }

            // Subscribe to market data for all symbols
            if (symbolsToSubscribe.Count > 0)
            {
                try
                {
                    _ibkrService.SubscribeToSymbols(symbolsToSubscribe);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not subscribe to IBKR market data, will use fallback if available");
                }

                // Update fallback playback with symbols if active
                var fallback = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
                if (fallback != null && fallback.IsActive)
                {
                    var currentSymbols = fallback.CurrentSymbols.ToList();
                    foreach (var symbol in symbolsToSubscribe)
                    {
                        if (!currentSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                        {
                            currentSymbols.Add(symbol);
                        }
                    }
                    fallback.UpdateSymbols(currentSymbols);
                }
            }

            _logger.LogInformation("Loaded {Count} symbols from watchlist into quotes", QuoteItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load quotes from watchlist");
            ErrorMessage = $"Failed to load watchlist: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task AddQuotesFromScannerAsync(IEnumerable<ScannerRowViewModel> rows)
    {
        try
        {
            if (rows == null) return;

            // Ensure we're on the Scanner option, not a watchlist
            if (SelectedWatchlist == null || SelectedWatchlist.Id != -1)
            {
                // Find and select the "Scanner" option
                var scannerOption = Watchlists.FirstOrDefault(w => w.Id == -1);
                if (scannerOption != null)
                {
                    SelectedWatchlist = scannerOption;
                    // Wait a moment for the watchlist change to process
                    await Task.Delay(50);
                }
            }

            // Build list to add (skip duplicates)
            foreach (var r in rows)
            {
                var symbol = r.Symbol?.Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(symbol)) continue;
                if (_rowCache.ContainsKey(symbol)) continue;

                var rowVm = new ScannerRowViewModel(_logger)
                {
                    Symbol = symbol,
                    Company = r.Company,
                    Region = r.Region,
                    Product = r.Product,
                    Exchange = r.Exchange,
                    IsDropped = false
                };

                // Seed with current values so UI shows something immediately; live ticks will update
                rowVm.LastPrice = r.LastPrice;
                rowVm.Volume = r.Volume;
                rowVm.AvgVolume = r.AvgVolume;
                // Seed previous close if available to enable Change/Change% immediately
                if (r.PrevClose > 0)
                    rowVm.UpdateClosePrice(r.PrevClose);

                _rowCache[symbol] = rowVm;
                QuoteItems.Add(rowVm);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add quotes from scanner");
            ErrorMessage = $"Failed to add quotes: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    public async Task SyncToSymbols(IEnumerable<string> symbols)
    {
        // Always set syncing flag to prevent restore during sync operations
        _isSyncing = true;
        try
        {
            // Ensure we're on the Scanner option, not a watchlist
            if (SelectedWatchlist == null || SelectedWatchlist.Id != -1)
            {
                // Find and select the "Scanner" option
                var scannerOption = Watchlists.FirstOrDefault(w => w.Id == -1);
                if (scannerOption != null)
                {
                    SelectedWatchlist = scannerOption;
                    // Wait a moment for the watchlist change to process
                    await Task.Delay(50);
                }
            }

            // Preserve order by converting to list first
            var orderedSymbols = symbols
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToUpperInvariant())
                .ToList();
            
            var target = new HashSet<string>(orderedSymbols, StringComparer.OrdinalIgnoreCase);

            // Try to get latest snapshots from fallback if available
            var fb = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
            var latest = fb != null ? fb.GetLatestSnapshots(target) : new Dictionary<string, TickData>();

            // Mark symbols not in target as dropped (instead of removing them)
            var toMarkAsDropped = _rowCache.Keys.Where(k => !target.Contains(k)).ToList();
            foreach (var k in toMarkAsDropped)
            {
                if (_rowCache.TryGetValue(k, out var vm))
                {
                    vm.IsDropped = true;
                }
            }

            // Add new or update existing (preserve order)
            foreach (var s in orderedSymbols)
            {
                if (_rowCache.TryGetValue(s, out var existingVm))
                {
                    // Mark as not dropped (in case it was previously dropped)
                    existingVm.IsDropped = false;
                    
                    // Update existing item with latest tick data if available
                    if (latest.TryGetValue(s, out var tick))
                    {
                        tick.ApplyTo(existingVm);
                        if (tick.PreviousClose.HasValue && tick.PreviousClose.Value > 0)
                            existingVm.UpdateClosePrice((double)tick.PreviousClose.Value);
                    }
                }
                else
                {
                    // Add new
                    var vm = new ScannerRowViewModel(_logger)
                    {
                        Symbol = s,
                        Company = s,
                        Region = "United States",
                        Product = "Stocks",
                        Exchange = "us stocks",
                        IsDropped = false
                    };
                    // Seed with latest tick data if available
                    if (latest.TryGetValue(s, out var tick))
                    {
                        tick.ApplyTo(vm);
                        if (tick.PreviousClose.HasValue && tick.PreviousClose.Value > 0)
                            vm.UpdateClosePrice((double)tick.PreviousClose.Value);
                    }
                    _rowCache[s] = vm;
                }
            }

            // Build ordered list: active symbols first (in scanner order), then dropped symbols
            var activeItems = new List<ScannerRowViewModel>();
            foreach (var s in orderedSymbols)
            {
                if (_rowCache.TryGetValue(s, out var vm) && !vm.IsDropped)
                {
                    activeItems.Add(vm);
                }
            }

            var droppedItems = _rowCache.Values
                .Where(vm => vm.IsDropped)
                .OrderBy(vm => vm.Symbol)
                .ToList();

            // Clear and rebuild QuoteItems: active first, then dropped (must be on UI thread)
            await _dispatcher.OnUIAsync(() =>
            {
                // Clear all items first to prevent duplicates
                QuoteItems.Clear();
                
                // Add active items first (in scanner order)
                foreach (var item in activeItems)
                {
                    QuoteItems.Add(item);
                }
                
                // Add dropped items below active ones
                foreach (var item in droppedItems)
                {
                    QuoteItems.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync quotes to symbols");
            ErrorMessage = $"Failed to sync quotes: {ex.Message}";
        }
        finally
        {
            _isSyncing = false;
        }
        await Task.CompletedTask;
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
        }
    }

    [RelayCommand]
    private async Task AddQuoteAsync()
    {
        try
        {
            // If a search result is highlighted, use that instead of raw text
            if (ShowSearchResults && SelectedSearchResultIndex >= 0 && SelectedSearchResultIndex < SearchResults.Count)
            {
                var selectedResult = SearchResults[SelectedSearchResultIndex];
                SelectSearchResult(selectedResult);
                // Continue to add the symbol (SelectSearchResult sets NewSymbolText)
            }
            
            var symbol = NewSymbolText?.Trim().ToUpperInvariant() ?? "";

            if (string.IsNullOrWhiteSpace(symbol))
            {
                ErrorMessage = "Please enter a symbol";
                return;
            }

            // Don't allow adding items during sync to prevent duplicates
            if (_isSyncing)
            {
                ErrorMessage = "Please wait for sync to complete";
                return;
            }

            // Check if already added
            if (_rowCache.ContainsKey(symbol))
            {
                ErrorMessage = $"Symbol {symbol} is already in quotes";
                return;
            }

            ErrorMessage = "";

            // Create ViewModel for this symbol
            var rowVm = new ScannerRowViewModel(_logger)
            {
                Symbol = symbol,
                IsDropped = false
            };

            _rowCache[symbol] = rowVm;
            QuoteItems.Add(rowVm);

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

                // Try to get latest snapshot from fallback to seed initial data (including PrevClose for Change/Change%)
                _logger.LogInformation("QuoteViewModel: Attempting to get snapshot for {Symbol} from fallback", symbol);
                var latest = fallback.GetLatestSnapshots(new[] { symbol });
                if (latest.TryGetValue(symbol, out var tick))
                {
                    _logger.LogInformation("QuoteViewModel: Found snapshot for {Symbol}: LastPrice={LastPrice}, ClosePrice={ClosePrice}, PreviousClose={PreviousClose}, Volume={Volume}", 
                        symbol, tick.LastPrice, tick.ClosePrice, tick.PreviousClose, tick.Volume);
                    tick.ApplyTo(rowVm);
                    if (tick.PreviousClose.HasValue && tick.PreviousClose.Value > 0)
                    {
                        _logger.LogInformation("QuoteViewModel: Setting PrevClose={PrevClose} for {Symbol} from snapshot", tick.PreviousClose.Value, symbol);
                        rowVm.UpdateClosePrice((double)tick.PreviousClose.Value);
                    }
                    _logger.LogInformation("QuoteViewModel: After snapshot apply, rowVm.PrevClose={PrevClose}, rowVm.LastPrice={LastPrice} for {Symbol}", 
                        rowVm.PrevClose, rowVm.LastPrice, symbol);
                }
                else
                {
                    _logger.LogWarning("QuoteViewModel: No snapshot data found in fallback for {Symbol}", symbol);
                }
            }
            else
            {
                _logger.LogInformation("QuoteViewModel: Fallback not active or not available for {Symbol}", symbol);
            }

            NewSymbolText = "";
            _logger.LogInformation("Added symbol {Symbol} to quotes (PrevClose={PrevClose}, LastPrice={LastPrice})", symbol, rowVm.PrevClose, rowVm.LastPrice);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add quote");
            ErrorMessage = $"Failed to add quote: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private void RemoveQuote(ScannerRowViewModel row)
    {
        try
        {
            var symbol = row.Symbol;
            QuoteItems.Remove(row);
            _rowCache.Remove(symbol);
            _logger.LogInformation("Removed symbol {Symbol} from quotes", symbol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove quote");
            ErrorMessage = $"Failed to remove quote: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearQuotes()
    {
        try
        {
            QuoteItems.Clear();
            _rowCache.Clear();
            ErrorMessage = "";
            _logger.LogInformation("Cleared all quotes");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear quotes");
            ErrorMessage = $"Failed to clear quotes: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunAlgoAsync(ScannerRowViewModel row)
    {
        try
        {
            if (_serviceProvider == null)
            {
                _logger.LogError("ServiceProvider is not available - cannot open algo runner");
                ErrorMessage = "Algo runner is not available";
                return;
            }

            // Get the current page to navigate from
            var currentPage = Application.Current?.MainPage;
            if (currentPage == null)
            {
                _logger.LogError("MainPage is not available - cannot open algo runner");
                ErrorMessage = "Cannot open algo runner - main page not available";
                return;
            }

            // Get candlestick builder and subscribe symbol (so candlesticks are built for this symbol)
            var candlestickBuilder = _serviceProvider.GetService<ICandlestickBuilder>();
            try
            {
                if (candlestickBuilder != null)
                {
                    candlestickBuilder.SubscribeSymbol(row.Symbol);
                    _logger.LogInformation("Subscribed {Symbol} to candlestick builder", row.Symbol);
                    
                    // Preload historical candlesticks so MACD can calculate immediately
                    await candlestickBuilder.PreloadCandlesticksAsync(row.Symbol);
                    _logger.LogInformation("Preloaded historical candlesticks for {Symbol}", row.Symbol);
                }
                else
                {
                    _logger.LogWarning("CandlestickBuilder not available - candlesticks may not be built for {Symbol}", row.Symbol);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe/preload {Symbol} to candlestick builder", row.Symbol);
                // Continue anyway - algo can still run without candlesticks (will return Hold)
            }

            // Create algo runner view model
            var algorithm = _serviceProvider.GetRequiredService<MarketScanner.Services.IAlgoStrategy>();
            var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
            var tradeService = _serviceProvider.GetService<ITradeService>();
            var algoRunnerViewModel = new AlgoRunnerViewModel(
                algorithm,
                loggerFactory.CreateLogger<AlgoRunnerViewModel>(),
                candlestickBuilder,
                _ibkrService,
                tradeService);

            // Initialize with selected symbol
            await algoRunnerViewModel.InitializeAsync(row);

            // Create and show algo runner page
            var algoRunnerPage = new Views.AlgoRunnerPage(algoRunnerViewModel);
            
            // Navigate to algo runner page
            await currentPage.Navigation.PushModalAsync(algoRunnerPage);

            _logger.LogInformation("Opened algo runner for symbol {Symbol}", row.Symbol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open algo runner");
            ErrorMessage = $"Failed to open algo runner: {ex.Message}";
        }
    }

    partial void OnNewSymbolTextChanged(string value)
    {
        // Clear error when user starts typing
        ErrorMessage = "";
        
        // Trigger search if 2+ characters
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
        {
            ShowSearchResults = false;
            SearchResults.Clear();
            return;
        }
        
        _ = PerformSearchAsync(value);
    }
    
    private async Task PerformSearchAsync(string query)
    {
        // Cancel previous search
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        
        try
        {
            // Debounce
            await Task.Delay(_searchDebounceDelay, _searchCts.Token);
            
            if (_symbolSearchService == null)
            {
                _logger.LogWarning("QuoteViewModel: Symbol search service is null - search cannot proceed");
                return;
            }
            
            if (_searchCts.Token.IsCancellationRequested)
            {
                return;
            }
                
            IsSearching = true;
            var results = await _symbolSearchService.SearchSymbolsAsync(query, _searchCts.Token);
            
            if (!_searchCts.Token.IsCancellationRequested)
            {
                await _dispatcher.OnUIAsync(() =>
                {
                    SearchResults.Clear();
                    foreach (var result in results)
                    {
                        SearchResults.Add(result);
                    }
                    ShowSearchResults = results.Count > 0;
                    SelectedSearchResultIndex = results.Count > 0 ? 0 : -1; // Auto-select first item
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user types again
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuoteViewModel: Symbol search failed for query: '{Query}'", query);
        }
        finally
        {
            IsSearching = false;
        }
    }
    
    partial void OnShowSearchResultsChanged(bool value)
    {
    }

    [RelayCommand]
    private void SelectSearchResult(SymbolSearchResult result)
    {
        NewSymbolText = result.Symbol;
        ShowSearchResults = false;
        SearchResults.Clear();
        SelectedSearchResultIndex = -1;
    }

    [RelayCommand]
    private void NavigateSearchResultsUp()
    {
        if (SearchResults.Count == 0) return;
        SelectedSearchResultIndex = SelectedSearchResultIndex <= 0 
            ? SearchResults.Count - 1 
            : SelectedSearchResultIndex - 1;
    }

    [RelayCommand]
    private void NavigateSearchResultsDown()
    {
        if (SearchResults.Count == 0) return;
        SelectedSearchResultIndex = (SelectedSearchResultIndex + 1) % SearchResults.Count;
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

        try
        {
            _batchTimer?.Stop();
            _batchTimer?.Dispose();
        }
        catch { }

        try
        {
            _tickSubscription?.Dispose();
        }
        catch { }

        try
        {
            _playbackSubscription?.Dispose();
        }
        catch { }

        try
        {
            _playbackSource?.Dispose();
        }
        catch { }

        QuoteItems.Clear();
        _rowCache.Clear();
    }
}
