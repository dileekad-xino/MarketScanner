using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketScanner.Core;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using MarketScanner.Utilities;
using System.Globalization;
using Microsoft.Extensions.Logging;
using System.Reactive.Linq;
using Microsoft.Maui.Controls;
using MarketScanner.Views;
using MarketScanner.Views.Dialogs;
using Microsoft.Maui.Dispatching;
namespace MarketScanner.ViewModels;

public partial class ScannerViewModel : ObservableObject
{
    private readonly IScanner _scanner;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger<ScannerViewModel> _logger;
    private readonly IWatchlistService _watchlistService;
    private readonly ITradeService _tradeService;
    private readonly IRsiSettingsService _rsiSettingsService;
    private readonly IAlgoStrategy _algoStrategy;
    private readonly Services.AlgoRunnerManagerService? _algoRunnerManager;
    private WatchlistViewModel? _watchlistViewModel;
    private QuoteViewModel? _quoteViewModel;
    private DailyPlViewModel? _dailyPlViewModel;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _filterCts;
    private int _applyEpoch; // NEW: prevents out-of-order commits
    private bool _disposed = false;
    private bool _isOffline = false;
    private bool _linkedQuotes = false;
    private SemaphoreSlim _syncQuotesSemaphore = new SemaphoreSlim(1, 1);

    // Store page title for dynamic watchlist naming and view title (set from code-behind and when switching views)
    [ObservableProperty] private string _pageTitle = "Market Scanner"; // fallback default

    // Track previous scan-level filters for hybrid filtering
    private (decimal minPrice, decimal maxPrice, string region, string product, string exchange, int topN)? _previousScanFilters;

    // Track symbols waiting for initial tick data (for MinChgPct filter)
    private HashSet<string>? _pendingInitialTicks;

    // UI batching for ultra-smooth updates (60 FPS)
    private readonly ConcurrentQueue<TickData> _batchedTicks = new();
    private IDispatcherTimer? _batchTimer;
    private readonly Dictionary<string, ScannerRowViewModel> _rowLookup = new();
    private bool _uiReady = false;

    private const int BatchIntervalMs = 16; // ~60 FPS for smooth updates
    private const int MaxBatchSize = 50;

    // ============================================================================
    // DATA ARCHITECTURE
    // ============================================================================
    // Snapshot: Immutable baseline from IBKR scan (ScannerRowViewModel[])
    //   - Replaced only when ScanFilters change (price, exchange, topN)
    //   - Updated in-place by live ticks (ChangePercent recalculated)
    //   - Source of truth for filtering
    //
    // ScannerItems (ShownData): Filtered/sorted view bound to UI
    //   - Derived from: Snapshot + ViewFilters
    //   - Updated instantly when ViewFilters change (MinChgPct, MinVolume)
    //   - ObservableCollection bound to CollectionView
    //
    // FILTER BUCKETS
    // ============================================================================
    // ScanFilters (trigger IBKR rescan):
    //   - MinPrice, MaxPrice, Exchange, TopN
    //   - Requires IBKR API call → replaces Snapshot
    //
    // ViewFilters (instant client-side):
    //   - MinChgPct (% change threshold), MinVolume
    //   - No IBKR call → derives ShownData from Snapshot
    // ============================================================================

    private ScannerRowViewModel[] _snapshot = Array.Empty<ScannerRowViewModel>();

    [ObservableProperty] private ObservableCollection<ScannerRowViewModel> _scannerItems = new();
    [ObservableProperty] private string _debugStatus = "";
    [ObservableProperty] private string _marketStatus = "Market Closed";

    // Filter properties

    [ObservableProperty] private string _exchange = "US Stocks";
    [ObservableProperty] private string _minPriceText = "";
    [ObservableProperty] private string _maxPriceText = "";
    [ObservableProperty] private string _minChangePercentText = "";
    [ObservableProperty] private string _volumeMinText = "";
    [ObservableProperty] private int _topN = 25;
    [ObservableProperty] private bool _isRefreshing = false;
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _autoRefreshEnabled = false;
    [ObservableProperty] private int _refreshIntervalSeconds = 60; // default 60

    // Expose ViewModels for binding
    public WatchlistViewModel? WatchlistViewModel => _watchlistViewModel;
    public QuoteViewModel? QuoteViewModel => _quoteViewModel;
    public DailyPlViewModel? DailyPlViewModel => _dailyPlViewModel;

    // Expose AlgoRunners collection for binding
    public ObservableCollection<AlgoRunnerViewModel> AlgoRunners =>
        _algoRunnerManager?.AlgoRunners ?? new ObservableCollection<AlgoRunnerViewModel>();

    // Properties for "more algos" indicator
    public bool HasMoreAlgos => _algoRunnerManager != null && _algoRunnerManager.Count > 3;
    public string MoreAlgosText => _algoRunnerManager != null && _algoRunnerManager.Count > 3
        ? $"+ {_algoRunnerManager.Count - 3} more algos"
        : "";

    // Options for pickers

    public List<string> ExchangeOptions { get; } = new() { "us stocks", "nasdaq", "nyse", "amex", "otc" };
    public List<int> TopNOptions { get; } = new() { 5, 10, 15, 20, 50 };

    private readonly Debounce _debounce = new(TimeSpan.FromMilliseconds(50)); // very responsive for production use
    private readonly Debounce _priceDebouncer = new(TimeSpan.FromMilliseconds(800)); // longer delay for price to prevent rescans on each keystroke

    // Auto-refresh timer fields
    private CancellationTokenSource? _autoCts;
    private Task? _autoTask;

    // Market status update timer
    private IDispatcherTimer? _marketStatusTimer;
    // Property change handlers - all use debounced filtering
    partial void OnMinChangePercentTextChanged(string value) => DebouncedApply();
    partial void OnVolumeMinTextChanged(string value) => DebouncedApply();

    // Auto-refresh property change handlers
    partial void OnRefreshIntervalSecondsChanged(int oldValue, int newValue)
    {
        // Persist selection
        Preferences.Set("refresh.interval.seconds", newValue);
        // If auto-refresh is on, restart quickly
        RestartAutoRefreshTimerIfNeeded();
    }

    partial void OnAutoRefreshEnabledChanged(bool oldValue, bool newValue)
    {
        Preferences.Set("refresh.enabled", newValue);
        if (newValue)
            RestartAutoRefreshTimerIfNeeded();
        else
        {
            // Stop auto-refresh immediately (synchronous cancellation)
            try { _autoCts?.Cancel(); } catch { }
            _autoTask = null;  // Don't wait for task, just null it
        }
    }

    public ScannerViewModel(
        IScanner scanner,
        IDispatcherService dispatcher,
        ILogger<ScannerViewModel> logger,
        IWatchlistService watchlistService,
        ITradeService tradeService,
        IRsiSettingsService rsiSettingsService,
        IAlgoStrategy algoStrategy,
        Services.AlgoRunnerManagerService? algoRunnerManager = null)
    {
        _scanner = scanner;
        _dispatcher = dispatcher;
        _logger = logger;
        _watchlistService = watchlistService;
        _tradeService = tradeService;
        _rsiSettingsService = rsiSettingsService;
        _algoStrategy = algoStrategy;
        _algoRunnerManager = algoRunnerManager;

        // Setup batch timer for ultra-smooth updates (60 FPS) FIRST
        _batchTimer = Application.Current.Dispatcher.CreateTimer();
        _batchTimer.Interval = TimeSpan.FromMilliseconds(BatchIntervalMs);
        _batchTimer.IsRepeating = true;
        _batchTimer.Tick += OnBatchTimerTick;

        // Don't start timer yet - wait for UI to be ready

        // Set production defaults AFTER timer is initialized
        SetProductionDefaults();

        // Subscribe to IBKR tick stream and queue for batching
        if (scanner is IbkrGatewayService ibkrGateway)
        {
            ibkrGateway.TickStream.Subscribe(tick =>
            {
                _batchedTicks.Enqueue(tick);
            });
        }

        // Subscribe to property changes for debounced filtering
        PropertyChanged += (_, e) =>
        {
            // Auto-reset exchange to "any" when region changes to non-US to avoid IBKR mismatch errors

            {

                {

                }
            }

            // Use longer debounce for price changes to prevent IBKR rescans on each keystroke
            if (e.PropertyName == nameof(MinPriceText) || e.PropertyName == nameof(MaxPriceText))
            {
                _ = _priceDebouncer.ExecuteAsync(ApplyFiltersAsync);
            }
            else if (e.PropertyName?.StartsWith("Min") == true ||
                        e.PropertyName?.StartsWith("Max") == true ||
                        e.PropertyName?.StartsWith("Selected") == true ||
                        e.PropertyName?.StartsWith("TopN") == true ||
                        e.PropertyName?.StartsWith("Exchange") == true)
            {
                DebouncedApply();
            }
        };

        // Load refresh preferences after initialization
        LoadRefreshPrefs();

        // Wire auto-refresh property changes
        WireAutoRefresh();

        // Initialize market status and set up periodic updates
        UpdateMarketStatus();
        SetupMarketStatusTimer();

        // Initialize all ViewModels on startup since they're always visible in tiled layout
        InitializeWatchlistView();
        InitializeQuoteView();
        InitializeDailyPlView();

        // Subscribe to AlgoRunner close events if manager is available
        if (_algoRunnerManager != null)
        {
            // Subscribe to collection changes to handle CloseTileRequested events
            _algoRunnerManager.AlgoRunners.CollectionChanged += (sender, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (AlgoRunnerViewModel algoRunner in e.NewItems)
                    {
                        algoRunner.CloseTileRequested += OnAlgoRunnerCloseRequested;
                    }
                }
                if (e.OldItems != null)
                {
                    foreach (AlgoRunnerViewModel algoRunner in e.OldItems)
                    {
                        algoRunner.CloseTileRequested -= OnAlgoRunnerCloseRequested;
                    }
                }
                // Notify that computed properties have changed
                OnPropertyChanged(nameof(HasMoreAlgos));
                OnPropertyChanged(nameof(MoreAlgosText));
                OnPropertyChanged(nameof(AlgoRunners));
            };

            // Also subscribe to property changes on the manager itself
            _algoRunnerManager.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(Services.AlgoRunnerManagerService.Count))
                {
                    OnPropertyChanged(nameof(HasMoreAlgos));
                    OnPropertyChanged(nameof(MoreAlgosText));
                }
            };
        }
    }

    private void OnAlgoRunnerCloseRequested(object? sender, EventArgs e)
    {
        if (sender is AlgoRunnerViewModel algoRunner && _algoRunnerManager != null)
        {
            _algoRunnerManager.RemoveAlgoRunner(algoRunner);
        }
    }

    private void SetProductionDefaults()
    {
        // Set production defaults for IBKR scanner

        Exchange = "us stocks";
        MinPriceText = "2";
        MaxPriceText = "20";
        VolumeMinText = "100000";
        TopN = 50;
        MinChangePercentText = "";  // No default change% filter

        _logger.LogInformation("Set production defaults: Price={MinPrice}-{MaxPrice}, Volume={VolumeMin}, TopN={TopN}",
            MinPriceText, MaxPriceText, VolumeMinText, TopN);
    }

    public void ResetToDefaults()
    {

        Exchange = "us stocks";
        MinPriceText = "2";
        MaxPriceText = "20";
        VolumeMinText = "100000";
        TopN = 50;

        _logger.LogInformation("Filters reset to production defaults");
    }

    private void DebouncedApply() => _ = _debounce.ExecuteAsync(ApplyFiltersAsync);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsRefreshing || IsLoading || _disposed) return;

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }

        _cts = new CancellationTokenSource();

        // Do NOT clear _rowLookup/_snapshot here. Clear only when we have new scan results
        // (inside OnUIAsync below). Otherwise a cancelled or failed ScanAsync would leave
        // _rowLookup empty and real-time tick updates would stop applying.

        try
        {
            IsRefreshing = true;
            ErrorMessage = "";
            DebugStatus = "Loading data...";

            // Start batch timer now that we're refreshing (UI should be ready)
            // StartBatchTimer();

            _logger.LogInformation("Starting data refresh...");

            // Get current scan-level filter values
            var minPrice = ParseDecimalSafe(MinPriceText) ?? 2;
            var maxPrice = ParseDecimalSafe(MaxPriceText) ?? 20;
            const string product = "stocks";  // Always stocks
            var exchange = IsAnyValue(Exchange) ? "us stocks" : Exchange.ToLowerInvariant();

            // Early check: if already offline, go directly to fallback
            if (_isOffline)
            {
                var fallback = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
                if (fallback != null && fallback.ActivateIfNeeded())
                {
                    var symbols = fallback.SelectSymbols(TopN, minPrice, maxPrice);
                    fallback.UpdateSymbols(symbols);
                    // Wait a moment for ticks to arrive if needed
                    var latest = await WaitForSnapshotsAsync(fallback, symbols, TimeSpan.FromMilliseconds(500));
                    await SeedFromFallbackAsync(symbols, latest);
                    await ApplyFiltersAsync();
                    if (_linkedQuotes) await SyncQuotesToVisibleAsync();
                    return;
                }
                ErrorMessage = "Fallback data source unavailable";
                return;
            }

            // Get fresh data using the scanner service with dynamic parameters
            // Cast to concrete type to access overloaded ScanAsync method
            IReadOnlyList<ScannerRow> rows;
            try
            {
                rows = (_scanner is IbkrGatewayService ibkrGateway)
                    ? await ibkrGateway.ScanAsync(minPrice, maxPrice, product, exchange, TopN, _cts.Token)
                    : await _scanner.ScanAsync(_cts.Token);
            }
            catch (Exception ex) when (IsConnectivityOrTimeout(ex))
            {
                // Offline fallback path
                _logger.LogWarning(ex, "Scan failed or timed out; activating playback fallback");
                _isOffline = true;
                var fallback = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
                if (fallback != null && fallback.ActivateIfNeeded())
                {
                    var symbols = fallback.SelectSymbols(TopN, minPrice, maxPrice);
                    fallback.UpdateSymbols(symbols);
                    // Wait a moment for ticks to arrive if needed
                    var latest = await WaitForSnapshotsAsync(fallback, symbols, TimeSpan.FromMilliseconds(500));
                    await SeedFromFallbackAsync(symbols, latest);
                    await ApplyFiltersAsync();
                    if (_linkedQuotes) await SyncQuotesToVisibleAsync();
                    return;
                }
                throw;
            }

            _logger.LogInformation("Received {Count} rows from scanner", rows.Count);

            // Clear existing rows and rebuild from scanner results
            try
            {
                await _dispatcher.OnUIAsync(() =>
                {
                    ScannerItems.Clear();
                    _rowLookup.Clear();

                foreach (var row in rows)
                {
                    var rowVm = new ScannerRowViewModel
                    {
                        Symbol = row.Symbol,
                        Company = row.Company ?? row.Symbol,
                        Region = "United States",  // Always US
                        Product = row.Meta.Product ?? "Stocks",
                        Exchange = row.Meta.Exchange ?? Exchange,
                        LastPrice = (double)row.LastPrice,
                        PrevClose = (double)row.LastPrice, // Will be updated by market data
                        Volume = (long)row.Volume,
                        AvgVolume = (long)row.AvgVolume
                    };
                    // RelativeVolume is auto-calculated in ScannerRowViewModel
                    _rowLookup[row.Symbol] = rowVm;
                    ScannerItems.Add(rowVm);
                }

                _logger.LogInformation("Created {Count} ScannerRowViewModel instances", ScannerItems.Count);

                // Store as immutable snapshot (baseline for filtering)
                _snapshot = ScannerItems.ToArray();
                });
            }
            catch (InvalidOperationException)
            {
                // Main thread not available - log and continue without updating UI
                // This can happen during app shutdown
                _logger.LogWarning("Unable to update UI - main thread not available (app may be shutting down)");
            }
            catch (Exception ex)
            {
                // Log any other exceptions but don't crash
                _logger.LogWarning(ex, "Error updating UI with scanner results");
            }

            // Re-apply client-side filters (TopN, MinChangePercent, Volume)
            // If MinChgPct filter is active, wait for all symbols to receive initial tick data
            if (!string.IsNullOrWhiteSpace(MinChangePercentText))
            {
                _pendingInitialTicks = new HashSet<string>(rows.Select(r => r.Symbol));
                _logger.LogInformation("MinChgPct filter active - waiting for {Count} symbols to receive initial tick data", _pendingInitialTicks.Count);
            }
            else
            {
                // No MinChgPct filter - apply other filters immediately
                await ApplyFiltersAsync();
                // Note: SyncQuotesToVisibleAsync is called at the end of ApplyFiltersAsync if _linkedQuotes is true
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Refresh cancelled; keeping current scanner rows for real-time updates");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during refresh");
            ErrorMessage = ex.Message;
            DebugStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
            DebugStatus = $"ScannerItems: {ScannerItems.Count}";
            _logger.LogInformation("RefreshAsync completed: ScannerItems={ScannerItemsCount}", ScannerItems.Count);
        }
    }

    private static bool IsConnectivityOrTimeout(Exception ex)
    {
        if (ex is TimeoutException) return true;
        var msg = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return msg.Contains("not connected") || msg.Contains("actively refused") || msg.Contains("timeout");
    }

    /// <summary>
    /// Waits for tick data to arrive for the given symbols, up to maxWait time.
    /// Returns snapshots immediately if available, otherwise waits and retries.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, TickData>> WaitForSnapshotsAsync(
        Services.Impl.PlaybackFallback fallback,
        IEnumerable<string> symbols,
        TimeSpan maxWait)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < maxWait)
        {
            var snapshots = fallback.GetLatestSnapshots(symbols);
            if (snapshots.Count > 0) return snapshots;
            await Task.Delay(50); // Check every 50ms
        }
        return fallback.GetLatestSnapshots(symbols); // Return whatever we have
    }

    private IEnumerable<string> DeriveFallbackSymbols(Services.Impl.PlaybackFallback fallback, int topN, decimal minPrice, decimal maxPrice)
    {
        // If we already saw some symbols in playback, prefer those; otherwise default set
        var current = fallback.CurrentSymbols;
        if (current != null && current.Count > 0)
            return current.Take(topN);
        return new[] { "AAPL", "MSFT", "NVDA", "AMD", "TSLA", "META", "AMZN", "GOOGL", "SPY", "QQQ" }.Take(topN);
    }

    private async Task SeedFromFallbackAsync(IEnumerable<string> symbols, IReadOnlyDictionary<string, TickData> latest)
    {
        var rows = new List<ScannerRowViewModel>();
        int scannerItemsCount = 0;
        int snapshotCount = 0;
        await _dispatcher.OnUIAsync(() =>
        {
            // Clear ScannerItems BEFORE building snapshot to ensure clean state
            ScannerItems.Clear();
            _rowLookup.Clear();

            foreach (var s in symbols)
            {
                var rowVm = new ScannerRowViewModel
                {
                    Symbol = s,
                    Company = s,
                    Region = "United States",
                    Product = "Stocks",
                    Exchange = Exchange
                };
                if (latest.TryGetValue(s, out var t))
                {
                    t.ApplyTo(rowVm);
                    if (t.PreviousClose.HasValue && t.PreviousClose.Value > 0)
                        rowVm.UpdateClosePrice((double)t.PreviousClose.Value);
                }
                else
                {
                    // No tick data yet - create row with zero values, will be updated by incoming ticks
                    rowVm.LastPrice = 0;
                    rowVm.Volume = 0;
                    rowVm.PrevClose = 0;
                }
                _rowLookup[s] = rowVm; // Populate lookup for tick updates
                rows.Add(rowVm); // Add to temp list, not ScannerItems
            }
            _snapshot = rows.ToArray(); // Set snapshot - ApplyFiltersAsync will populate ScannerItems from this
            snapshotCount = _snapshot.Length;
            scannerItemsCount = ScannerItems.Count;

            // Verify ScannerItems is still empty (defensive check)
            if (scannerItemsCount != 0)
            {
                _logger.LogWarning("ScannerItems not empty after seeding! Count={Count}, expected 0. Clearing again.", scannerItemsCount);
                ScannerItems.Clear();
                scannerItemsCount = 0;
            }
        });

        // Ensure we are consuming fallback stream live
        var fb = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
        if (fb != null)
        {
            fb.TickStream.Subscribe(t => _batchedTicks.Enqueue(t));
        }

        // Note: ApplyFiltersAsync will be called by RefreshAsync after this returns
        // if (_linkedQuotes) await SyncQuotesToVisibleAsync(); // Moved to RefreshAsync after ApplyFiltersAsync
        _logger.LogInformation("Fallback seeding complete: ScannerItems={Count}, Snapshot={SnapshotCount}", scannerItemsCount, snapshotCount);
    }

    private void FlushBatchedTicks()
    {
        if (!_uiReady) return;

        _dispatcher.OnUI(() =>
        {
            if (_disposed || !_uiReady) return;

            var processedCount = 0;
            var updatedSymbols = new HashSet<string>();

            while (_batchedTicks.TryDequeue(out var tick) &&
                   processedCount < MaxBatchSize)
            {
                if (_disposed) return;
                if (!_rowLookup.TryGetValue(tick.Symbol, out var rowVm))
                    continue;

                tick.ApplyTo(rowVm);

                if (updatedSymbols.Add(tick.Symbol))
                    processedCount++;

                if (_pendingInitialTicks != null &&
                    rowVm.PrevClose > 0 &&
                    _pendingInitialTicks.Remove(tick.Symbol))
                {
                    if (_pendingInitialTicks.Count == 0)
                    {
                        _pendingInitialTicks = null;

                        _ = _dispatcher.OnUIAsync(async () =>
                        {
                            await ApplyFiltersAsync();
                        });
                    }
                }
            }

            if (!_disposed && processedCount > 0)
            {
                DebugStatus = $"Updated {updatedSymbols.Count} symbols ({processedCount} ticks)";
            }
        });
    }

    public void StartBatchTimer()
    {
        if (_disposed || _batchTimer.IsRunning)
            return;

        _uiReady = true;
        _batchTimer.Start();

        _logger.LogInformation("Batch timer started");
    }



    private ScannerRowViewModel GetOrCreateRow(string symbol)
    {
        if (!_rowLookup.TryGetValue(symbol, out var rowVm))
        {
            rowVm = new ScannerRowViewModel
            {
                Symbol = symbol,
                Company = symbol,
                Region = "United States",  // Always US
                Product = "Stocks",
                Exchange = Exchange
            };
            _rowLookup[symbol] = rowVm;
            // DO NOT add to ScannerItems here - only ApplyFiltersAsync should populate ScannerItems
            // This ensures filtered-out items don't reappear when ticks arrive
        }
        return rowVm;
    }

    private static bool IsAnyValue(string? s) =>
        string.IsNullOrWhiteSpace(s)
        || s.Equals("any", StringComparison.OrdinalIgnoreCase)
        || s.Equals("all", StringComparison.OrdinalIgnoreCase)
        || s.Equals("us", StringComparison.OrdinalIgnoreCase)
        || s.Equals("stocks", StringComparison.OrdinalIgnoreCase)
        || s.Equals("us stocks", StringComparison.OrdinalIgnoreCase)
        || s.Equals("-", StringComparison.OrdinalIgnoreCase);

    private static decimal? ParseDecimalSafe(string? s) => Parsing.ParseDecimal(s);
    private static decimal? ParsePercentSafe(string? s) => Parsing.ParsePercent(s);

    /// <summary>
    /// Determines if current filter changes require IBKR rescan (ScanFilters).
    /// ScanFilters: price range, exchange, TopN → Replace Snapshot
    /// ViewFilters: MinChgPct, volume → Derive ShownData from Snapshot
    /// </summary>
    private bool RequiresRescan()
    {
        var currentMinPrice = ParseDecimalSafe(MinPriceText) ?? 2;
        var currentMaxPrice = ParseDecimalSafe(MaxPriceText) ?? 20;
        const string currentRegion = "us";  // Always US
        const string currentProduct = "stocks";  // Always stocks
        var currentExchange = IsAnyValue(Exchange) ? "us stocks" : Exchange.ToLowerInvariant();
        var currentTopN = TopN;
        // MinChgPct removed - it's client-side only, no re-scan needed

        var currentScanFilters = (currentMinPrice, currentMaxPrice, currentRegion, currentProduct, currentExchange, currentTopN);

        // If no previous scan, we need to scan
        if (_previousScanFilters == null)
        {
            _previousScanFilters = currentScanFilters;
            return true;
        }

        // Check if scan-level filters changed
        var requiresRescan = _previousScanFilters.Value != currentScanFilters;

        if (requiresRescan)
        {
            _logger.LogInformation("Scan-level filters changed: {Previous} -> {Current}, triggering re-scan",
                _previousScanFilters.Value, currentScanFilters);
            _previousScanFilters = currentScanFilters;
        }

        return requiresRescan;
    }

    /// <summary>
    /// Applies ViewFilters to Snapshot and updates ShownData (ScannerItems).
    /// Always filters from Snapshot (not from filtered results) to support
    /// both tightening (10%→15%) and loosening (15%→10%) of filters.
    /// </summary>
    private async Task ApplyFiltersAsync()
    {
        if (_disposed) return;

        // Check if we need to re-scan at IBKR level
        if (RequiresRescan())
        {
            if (_isOffline)
            {
                // Reseed from fallback instead of IBKR
                var fb = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
                if (fb != null && fb.ActivateIfNeeded())
                {
                    var minPrice = ParseDecimalSafe(MinPriceText) ?? 2;
                    var maxPrice = ParseDecimalSafe(MaxPriceText) ?? 20;
                    var symbols = fb.SelectSymbols(TopN, minPrice, maxPrice);
                    fb.UpdateSymbols(symbols);
                    var latest = fb.GetLatestSnapshots(symbols);
                    await SeedFromFallbackAsync(symbols, latest);
                    // Continue to FilterEngine logic below to apply TopN and other filters
                    // (Don't return early - let FilterEngine apply TopN from the seeded pool)
                }
                else
                {
                    return; // No fallback available
                }
            }
            else
            {
                _logger.LogInformation("Significant filter changes detected, triggering re-scan");
                await RefreshAsync();
                return;
            }
        }

        // If no items in snapshot or ScannerItems, skip client-side filtering
        // Check _snapshot.Length first since that's the source of truth after reseeding
        if (_snapshot.Length == 0 && ScannerItems.Count == 0)
        {
            _logger.LogInformation("ApplyFiltersAsync skipped - no items to filter");
            return;
        }

        _logger.LogInformation("ApplyFiltersAsync started with {Count} items (client-side filtering only)", ScannerItems.Count);

        var epoch = Interlocked.Increment(ref _applyEpoch);

        // Build criteria (treat default labels as no-op)
        // Note: TopN is now handled at IBKR level, not client-side
        var criteria = new FilterEngine.Criteria(

            Exchange: IsAnyValue(Exchange) ? null : Exchange,
            MinPrice: ParseDecimalSafe(MinPriceText),
            MaxPrice: ParseDecimalSafe(MaxPriceText),
            MinChgPct: ParsePercentSafe(MinChangePercentText),
            MinVolume: ParseDecimalSafe(VolumeMinText) != null ? (long)ParseDecimalSafe(VolumeMinText)!.Value : null,
            TopN: TopN // Pass the actual TopN value from ViewModel
        );

        _logger.LogInformation("Filter criteria: MinPrice={MinPrice}, MaxPrice={MaxPrice}, MinVolume={MinVolume}, MinChgPct={MinChgPct}, TopN={TopN}",
            criteria.MinPrice, criteria.MaxPrice, criteria.MinVolume, criteria.MinChgPct, criteria.TopN);

        // Always derive ShownData from Snapshot (not from filtered results)
        // This allows loosening filters (15% → 10%) to show previously hidden stocks
        var rows = _snapshot.Length > 0 ? _snapshot : ScannerItems.ToArray();

        // Log all snapshot data with ChangePercent values
        _logger.LogInformation("=== SNAPSHOT DATA ({Count} stocks) ===", rows.Length);
        foreach (var row in rows.OrderByDescending(r => r.ChangePercent))
        {
            _logger.LogInformation("  {Symbol}: Price=${Price:F2}, Change={Change:F2}%, Volume={Volume:N0}",
                row.Symbol, row.LastPrice, row.ChangePercent, row.Volume);
        }

        // Cancel previous filter operation and create new one
        try
        {
            _filterCts?.Cancel();
            _filterCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }
        _filterCts = new CancellationTokenSource();
        var token = _filterCts?.Token ?? CancellationToken.None;

        // Check if cancelled before expensive operation
        if (_filterCts?.IsCancellationRequested == true) return;

        var result = await Task.Run(() => FilterEngine.Apply(rows, criteria, _logger), token);

        if (epoch != _applyEpoch)
        {
            _logger.LogInformation("ApplyFiltersAsync cancelled - stale compute");
            return; // stale compute – ignore
        }

        _logger.LogInformation("FilterEngine.Apply returned {FilteredCount} of {TotalCount} items", result.TopIndices.Length, rows.Length);

        // Log filtered results
        _logger.LogInformation("=== FILTERED RESULTS ({Count} stocks passed) ===", result.TopIndices.Length);
        foreach (var i in result.TopIndices.OrderByDescending(idx => rows[idx].ChangePercent))
        {
            var row = rows[i];
            _logger.LogInformation("  {Symbol}: Price=${Price:F2}, Change={Change:F2}%, Volume={Volume:N0}",
                row.Symbol, row.LastPrice, row.ChangePercent, row.Volume);
        }

        // Marshal to UI thread once with the FILTERED set
        await _dispatcher.OnUIAsync(() =>
        {
            ScannerItems.Clear();
            foreach (var i in result.TopIndices)
                ScannerItems.Add(rows[i]);

            _logger.LogInformation("ScannerItems updated on UI thread with {Count} items", ScannerItems.Count);

            DebugStatus =
                $"Items: {rows.Length} | Shown: {ScannerItems.Count} | TopN={criteria.TopN} " +
                $"| Price=[{criteria.MinPrice?.ToString() ?? "-"}, {criteria.MaxPrice?.ToString() ?? "-"}] " +
                $"| Vol>={(criteria.MinVolume?.ToString() ?? "-")}";
        });

        if (_linkedQuotes)
            await SyncQuotesToVisibleAsync();
    }

    private async Task SyncQuotesToVisibleAsync()
    {
        // Prevent concurrent execution to avoid race conditions
        if (!await _syncQuotesSemaphore.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_quoteViewModel == null)
            {
                return;
            }

            // Preserve order from ScannerItems (ObservableCollection maintains order)
            var visible = ScannerItems
                .Select(r => r.Symbol)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray(); // Convert to array to ensure order is maintained

            await _quoteViewModel.SyncToSymbols(visible);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed syncing quotes to visible symbols");
        }
        finally
        {
            _syncQuotesSemaphore.Release();
        }
    }


    [RelayCommand]
    private async Task LoadDataAsync()
    {
        if (IsLoading) return;
        try
        {
            IsLoading = true;
            await RefreshAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Set page title for dynamic watchlist naming
    /// </summary>
    public void SetPageTitle(string title)
    {
        PageTitle = title;
        _logger.LogDebug("Page title set to: {Title}", title);
    }

    /// <summary>
    /// Load refresh preferences from storage
    /// </summary>
    public void LoadRefreshPrefs()
    {
        RefreshIntervalSeconds = Preferences.Get("refresh.interval.seconds", 60);
        AutoRefreshEnabled = Preferences.Get("refresh.enabled", false);
    }

    /// <summary>
    /// Wire auto-refresh property changes to trigger refresh logic
    /// </summary>
    private void WireAutoRefresh()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AutoRefreshEnabled) || e.PropertyName == nameof(RefreshIntervalSeconds))
            {
                RestartAutoRefreshTimerIfNeeded();
            }
        };
    }

    /// <summary>
    /// Stop the auto-refresh timer
    /// </summary>
    public async Task StopAutoRefreshAsync()
    {
        try
        {
            _autoCts?.Cancel();
            _autoCts?.Dispose();
            _autoCts = null;
        }
        catch { }

        if (_autoTask != null)
        {
            try { await _autoTask; } catch { }
            _autoTask = null;
        }
    }

    /// <summary>
    /// Restart the auto-refresh timer if enabled
    /// </summary>
    public void RestartAutoRefreshTimerIfNeeded()
    {
        _ = Task.Run(async () =>
        {
            await StopAutoRefreshAsync();
            if (!AutoRefreshEnabled) return;

            var cts = new CancellationTokenSource();
            var token = cts.Token;  // Capture token BEFORE assigning to field
            _autoCts = cts;  // Now assign to field

            // Use captured token (safe even if cts gets disposed)
            _autoTask = Task.Run(() => RunAutoRefreshLoopAsync(token));
        });
    }

    /// <summary>
    /// Run the auto-refresh loop with proper error handling
    /// </summary>
    private async Task RunAutoRefreshLoopAsync(CancellationToken ct)
    {
        // Start with immediate refresh
        await RefreshAsync().ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var delay = Math.Max(5, RefreshIntervalSeconds); // floor to 5s
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    // If fallback is active, re-sync symbols based on current filters
                    var fb = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services?.GetService<MarketScanner.Services.Impl.PlaybackFallback>();
                    if (fb != null && (fb.IsActive || _isOffline))
                    {
                        // Always read current filter values fresh - don't use cached/defaults
                        var minPrice = ParseDecimalSafe(MinPriceText) ?? 2;
                        var maxPrice = ParseDecimalSafe(MaxPriceText) ?? 20;
                        var topN = TopN; // Use current TopN value from ViewModel
                        _logger.LogInformation("Auto-refresh using filters: MinPrice={MinPrice}, MaxPrice={MaxPrice}, TopN={TopN}", minPrice, maxPrice, topN);
                        var symbols = fb.SelectSymbols(topN, minPrice, maxPrice);
                        fb.UpdateSymbols(symbols);
                        var latest = await WaitForSnapshotsAsync(fb, symbols, TimeSpan.FromMilliseconds(500));
                        await SeedFromFallbackAsync(symbols, latest).ConfigureAwait(false);
                        await ApplyFiltersAsync().ConfigureAwait(false);
                        if (_linkedQuotes) await SyncQuotesToVisibleAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        // Only call RefreshAsync when not offline - it will handle IBKR connection
                        await RefreshAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (TaskCanceledException) { }
        }
    }

    /// <summary>
    /// Handle auto-refresh toggle
    /// </summary>
    public async Task OnAutoRefreshToggledAsync(bool isEnabled)
    {
        if (isEnabled)
            RestartAutoRefreshTimerIfNeeded();
        else
            await StopAutoRefreshAsync();
    }

    private IRelayCommand? _resetFiltersCommand;
    public IRelayCommand ResetFiltersCommand => _resetFiltersCommand ??=
        new RelayCommand(async () =>
        {

            Exchange = "us stocks";
            MinPriceText = "2";
            MaxPriceText = "20";
            VolumeMinText = "100000";
            MinChangePercentText = "";
            TopN = 50;

            await ApplyFiltersAsync();

            // Stop auto-refresh timer when resetting filters
            await StopAutoRefreshAsync();
        });

    /// <summary>
    /// Switches to the scanner view.
    /// </summary>

    /// <summary>
    /// Initializes the watchlist ViewModel on-demand.
    /// </summary>
    private void InitializeWatchlistView()
    {
        if (_watchlistViewModel != null) return;

        _logger.LogDebug("Creating WatchlistViewModel on UI thread");

        // Get service provider for symbol search service
        var serviceProvider = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;
        var symbolSearchService = serviceProvider?.GetService<ISymbolSearchService>();

        // ✅ Create ViewModel synchronously on UI thread (ready for binding)
        _watchlistViewModel = new WatchlistViewModel(
            _watchlistService,
            (IbkrGatewayService)_scanner,
            _dispatcher,
            Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole())
                .CreateLogger<WatchlistViewModel>(),
            symbolSearchService);

        _logger.LogDebug("WatchlistViewModel created, notifying property change");
        OnPropertyChanged(nameof(WatchlistViewModel));

        // ✅ Initialize database async (fire-and-forget, no Task.Run wrapper)
        _ = _watchlistViewModel.InitializeAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Failed to initialize watchlist database");
            }
            else
            {
                _logger.LogDebug("WatchlistViewModel database initialized successfully");
            }
        }, TaskScheduler.Default);

        _logger.LogInformation("Watchlist view initialized, database loading in background");
    }


    /// <summary>
    /// Initializes the quote ViewModel on-demand.
    /// </summary>
    private void InitializeQuoteView()
    {
        if (_quoteViewModel != null) return;

        _logger.LogDebug("Creating QuoteViewModel on UI thread");

        // Get service provider for algo runner
        var serviceProvider = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;

        // Create ViewModel synchronously on UI thread (ready for binding)
        // Get logger from service provider to use the main app's logging configuration
        var loggerFactory = serviceProvider?.GetService<ILoggerFactory>();
        var quoteLogger = loggerFactory?.CreateLogger<QuoteViewModel>()
            ?? Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<QuoteViewModel>();

        var symbolSearchService = serviceProvider?.GetService<ISymbolSearchService>();

        _quoteViewModel = new QuoteViewModel(
            (IbkrGatewayService)_scanner,
            _dispatcher,
            _watchlistService,
            quoteLogger,
            serviceProvider,
            symbolSearchService);

        _logger.LogDebug("QuoteViewModel created, notifying property change");
        OnPropertyChanged(nameof(QuoteViewModel));

        // Initialize async (fire-and-forget)
        _ = _quoteViewModel.InitializeAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Failed to initialize quote view");
            }
            else
            {
                _logger.LogDebug("QuoteViewModel initialized successfully");
            }
        }, TaskScheduler.Default);

        _logger.LogInformation("Quote view initialized");
    }

    /// <summary>
    /// Initializes the Daily P/L ViewModel on-demand.
    /// </summary>
    private void InitializeDailyPlView()
    {
        if (_dailyPlViewModel != null) return;

        _logger.LogDebug("Creating DailyPlViewModel on UI thread");

        var serviceProvider = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;
        var loggerFactory = serviceProvider?.GetService<ILoggerFactory>();
        var dailyPlLogger = loggerFactory?.CreateLogger<DailyPlViewModel>()
            ?? Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<DailyPlViewModel>();

        // Create ViewModel synchronously on UI thread
        _dailyPlViewModel = new DailyPlViewModel(
            _tradeService,
            dailyPlLogger);

        _logger.LogDebug("DailyPlViewModel created, notifying property change");
        OnPropertyChanged(nameof(DailyPlViewModel));

        // Initialize async (fire-and-forget)
        _ = _dailyPlViewModel.InitializeAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogError(t.Exception, "Failed to initialize Daily P/L view");
            }
            else
            {
                _logger.LogDebug("DailyPlViewModel initialized successfully");
            }
        }, TaskScheduler.Default);

        _logger.LogInformation("Daily P/L view initialized");
    }

    /// <summary>
    /// Add all currently visible scanner items to the Quotes panel and switch to the full Quote view.
    /// </summary>
    [RelayCommand]
    private async Task OpenDailyPlWindowAsync()
    {
        try
        {
            // Ensure DailyPlViewModel is initialized
            if (_dailyPlViewModel == null)
            {
                InitializeDailyPlView();
            }

            if (_dailyPlViewModel == null)
            {
                _logger.LogError("DailyPlViewModel is not available");
                return;
            }

            // Get the current window/page
            var page = Application.Current?.MainPage;
            if (page == null)
            {
                _logger.LogError("MainPage is not available - cannot open Daily P/L window");
                return;
            }

            // Create the Daily P/L window page
            var dailyPlPage = new DailyPlWindowPage(_dailyPlViewModel);
            var dailyPlWindow = new Window(dailyPlPage)
            {
                Title = "Daily P/L"
            };

            // Open the window
            Application.Current?.OpenWindow(dailyPlWindow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Daily P/L window");
        }
    }

    [RelayCommand]
    private async Task AddAllVisibleToQuotesAsync()
    {
        try
        {
            // Ensure QuoteViewModel exists
            if (_quoteViewModel == null)
            {
                InitializeQuoteView();
            }

            if (_quoteViewModel == null)
            {
                _logger.LogError("QuoteViewModel is not available");
                return;
            }

            // Take a snapshot of currently visible items
            var rows = ScannerItems?.ToArray() ?? Array.Empty<ScannerRowViewModel>();
            if (rows.Length == 0)
            {
                _logger.LogInformation("No visible scanner items to add to Quotes");
            }
            else
            {
                await _quoteViewModel.AddQuotesFromScannerAsync(rows);
            }

            // After bulk-add, link quotes to scanner
            _linkedQuotes = true;

            // Switch to full Quotes view (do not alter the scanner right panel state beyond view switch)
            // View switching removed - all views are now always visible in tiled layout
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add visible items to Quotes");
        }
    }

    /// <summary>
    /// Generate next available watchlist name using page title
    /// </summary>
    private async Task<string> GenerateWatchlistNameAsync()
    {
        await _watchlistService.InitializeAsync();
        var watchlists = await _watchlistService.GetAllWatchlistsAsync();

        var baseName = PageTitle; // Uses page Title dynamically!
        var existingNames = watchlists.Select(w => w.Name).ToHashSet();
        var number = 1;
        string newName;

        do
        {
            newName = $"{baseName} {number}";
            number++;
        } while (existingNames.Contains(newName));

        return newName;
    }

    [RelayCommand]
    private async Task AddToWatchlistAsync(ScannerRowViewModel clickedRow)
    {
        try
        {
            // Get ALL visible stocks with their company names
            var symbolsToAdd = ScannerItems.Select(r => (r.Symbol, r.Company)).ToList();

            _logger.LogInformation("User double-clicked {Symbol}, adding ALL {Count} visible symbols to watchlist",
                clickedRow.Symbol, symbolsToAdd.Count);

            // Generate watchlist name using page title
            var newName = await GenerateWatchlistNameAsync();

            // Create watchlist with auto-generated name
            var newWatchlist = await _watchlistService.CreateWatchlistAsync(newName);

            // Add all visible stocks with company names
            await _watchlistService.AddItemsAsync(newWatchlist.Id, symbolsToAdd);

            _logger.LogInformation("Created watchlist '{Name}' with {Count} symbols",
                newName, symbolsToAdd.Count);

            // TODO: Show toast notification to user
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add to watchlist");
        }
    }

    [RelayCommand]
    private async Task ShowRsiSettingsAsync(ScannerRowViewModel? row)
    {
        try
        {
            var page = Application.Current?.MainPage;
            if (page == null)
            {
                _logger.LogWarning("Cannot show RSI settings - MainPage is null");
                return;
            }

            var current = await _rsiSettingsService.GetAsync();
            var popup = new RsiSettingsPopup(current);
            var result = await page.ShowPopupAsync(popup);
            if (result is RsiSettings updated)
            {
                await _rsiSettingsService.SaveAsync(updated);
                if (row != null)
                {
                    await AnalyzeSelectedRowAsync(row);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to present RSI settings dialog");
        }
    }

    /// <summary>
    /// Updates the market status property based on current time.
    /// </summary>
    private void UpdateMarketStatus()
    {
        var status = MarketHours.GetMarketStatus();
        MarketStatus = MarketHours.GetMarketStatusString(status);
    }

    /// <summary>
    /// Sets up a timer to update market status every 60 seconds.
    /// </summary>
    private void SetupMarketStatusTimer()
    {
        if (_marketStatusTimer != null)
            return;

        _marketStatusTimer = Application.Current.Dispatcher.CreateTimer();
        _marketStatusTimer.Interval = TimeSpan.FromMinutes(1);
        _marketStatusTimer.IsRepeating = true;

        _marketStatusTimer.Tick += OnMarketStatusTimerTick;
        _marketStatusTimer.Start();

        _logger.LogInformation("Market status timer started");
    }

    private void OnMarketStatusTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        UpdateMarketStatus();
    }


    private void OnBatchTimerTick(object? sender, EventArgs e)
    {
        FlushBatchedTicks();
    }


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _snapshot = Array.Empty<ScannerRowViewModel>();

        // Dispose with exception handling to prevent ObjectDisposedException
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _cts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        // Ensure proper disposal order for filter CTS
        try
        {
            _filterCts?.Cancel();
            Task.Delay(50).Wait();  // Give pending operations time to cancel
        }
        catch (ObjectDisposedException) { }

        try
        {
            _filterCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        // Stop batch timer
        try
        {
            if (_batchTimer != null)
            {
                _batchTimer.Tick -= OnBatchTimerTick;
                _batchTimer.Stop();
                _batchTimer = null;
            }

        }
        catch (ObjectDisposedException) { }

        try
        {
            _ = StopAutoRefreshAsync();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _autoCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _debounce.Dispose();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _priceDebouncer.Dispose();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _syncQuotesSemaphore?.Dispose();
        }
        catch (ObjectDisposedException) { }

        // Stop market status timer
        try
        {
            if (_marketStatusTimer != null)
            {
                _marketStatusTimer.Tick -= OnMarketStatusTimerTick;
                _marketStatusTimer.Stop();
                _marketStatusTimer = null;
            }

        }
        catch (ObjectDisposedException) { }

        // Stop scanner
        try
        {
            _scanner.StopAsync().ConfigureAwait(false);
        }
        catch { }

    }

    private async Task AnalyzeSelectedRowAsync(ScannerRowViewModel row)
    {
        try
        {
            var result = await _algoStrategy.ExecuteAsync(row);
            if (result != null)
            {
                row.RsiValue = result.RsiValue;
                row.RsiSignal = result.RsiSignal;
                row.CciValue = result.CciValue;
                row.CciSignal = result.CciSignal;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update RSI for symbol {Symbol}", row.Symbol);
        }
    }
}
