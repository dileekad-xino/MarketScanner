using IBApi;
using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Globalization;
using System.IO;


namespace MarketScanner.Services.Ibkr;

/// <summary>
/// Unified IBKR gateway service that implements EWrapper directly.
/// Combines scanner and market data functionality in a single, maintainable service.
/// Based on proven patterns from backup and working Node.js implementation.
/// </summary>
public sealed class IbkrGatewayService : EWrapper, IScanner, IMarketDataService, IDisposable
{
    // Tick field constants (from working Node.js implementation)
    private const int TICK_LAST = 4;
    private const int TICK_CLOSE = 9;
    private const int TICK_VOLUME = 8;
    private const int TICK_OPEN = 14;
    private const int TICK_HIGH = 6;
    private const int TICK_LOW = 7;
    private const int TICK_DELAYED_LAST = 68;
    private const int TICK_DELAYED_CLOSE = 75;
    private const int TICK_DELAYED_VOLUME = 74;
    private const int TICK_DELAYED_OPEN = 73;
    private const int TICK_DELAYED_HIGH = 69;
    private const int TICK_DELAYED_LOW = 70;
    private const int TICK_RT_VOLUME = 48;
    private const int TICK_DELAYED_RT_VOLUME = 77;
    private const int US_STOCK_VOLUME_MULTIPLIER = 100;
    private const int AvgVolumeLookbackTradingDays = 30;
    private const string AvgVolumeRequestDuration = "60 D";
    // Minimum practical window to reliably capture >=30 trading sessions (weekends/holidays included).
    private const string ScannerAvgVolumeRequestDuration = "30 D";
    private static readonly TimeSpan ScannerResultCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoricalCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ScannerRequestTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan ScannerQuietPeriod = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan CallbackStaleThreshold = TimeSpan.FromSeconds(15);

    private readonly ILogger<IbkrGatewayService> _logger;
    private readonly IConfiguration _config;

    private EClientSocket _client = default!;
    private EReaderSignal _signal = default!;
    private bool _connected;
    private int _nextValidId;
    private int _nextReqId = 1;
    private bool _disposed;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private Task? _readerLoopTask;
    private CancellationTokenSource? _readerLoopCts;
    private readonly object _readerLoopLock = new();
    private readonly object _connectionStatusLock = new();
    private bool? _lastPublishedConnectionStatus;
    private CancellationTokenSource? _reconnectLoopCts;
    private Task? _reconnectLoopTask;
    private long _lastCallbackUtcTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// Gets whether the service is connected to IBKR gateway.
    /// Returns true only if connection was successfully established (nextValidId > 0).
    /// </summary>
    public bool IsConnected => _connected && _nextValidId > 0 && _client != null && _client.IsConnected();
    public event Action<bool, string>? ConnectionStateChanged;

    // Scanner state
    private readonly ConcurrentDictionary<int, List<ScannerRow>> _scannerBuffers = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<List<ScannerRow>>> _scannerWaiters = new();
    private readonly ConcurrentDictionary<int, ScannerRow> _scannerResults = new();
    private readonly ConcurrentDictionary<int, bool> _scannerRequestIds = new(); // Track all scanner request IDs for Error 162 suppression
    private readonly ConcurrentDictionary<int, int> _scannerRequestedRows = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _scannerQuietTimers = new();
    private readonly ConcurrentDictionary<int, int> _scannerDataCounts = new();
    private readonly ConcurrentDictionary<int, byte> _scannerDataEndSeen = new();
    private readonly ConcurrentDictionary<int, int> _scannerLastErrorCodes = new();
    private int _currentScannerId = 0; // Track current active scanner for cancellation
    private readonly SemaphoreSlim _scannerExecutionGate = new(1, 1);
    private readonly object _scanCacheLock = new();
    private string? _lastScanCacheKey;
    private DateTimeOffset _lastScanAtUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<ScannerRow> _lastScanRows = Array.Empty<ScannerRow>();

    // Market data state
    private readonly ConcurrentDictionary<int, string> _idToSymbol = new();
    private readonly ConcurrentDictionary<string, MarketState> _marketState = new();
    private readonly ConcurrentDictionary<string, SnapshotRow> _snapshots = new();
    private readonly ConcurrentDictionary<string, long> _averageVolumes = new();
    private int _nextManualTickerId = 20000; // Separate counter for manually added symbols (scanner uses 10000-19999)

    // Historical data tracking
    private readonly ConcurrentDictionary<int, string> _histReqToSymbol = new();
    private readonly ConcurrentDictionary<int, List<long>> _histVolumes = new();
    private readonly ConcurrentDictionary<int, double?> _histClosePrices = new(); // Track most recent close from historical data
    private readonly ConcurrentDictionary<string, HistoricalCacheEntry> _historicalCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _historicalInFlight = new(StringComparer.OrdinalIgnoreCase);

    // Historical bars tracking for RSI and other technical indicators
    private readonly ConcurrentDictionary<int, List<Bar>> _histBars = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<List<Bar>>> _histBarWaiters = new();

    // Symbol search state
    private readonly ConcurrentDictionary<int, TaskCompletionSource<List<SymbolSearchResult>>> _symbolSearchWaiters = new();
    private readonly Dictionary<int, List<SymbolSearchResult>> _symbolSearchBuffers = new();
    private int _nextSearchReqId = 20000; // Start from high ID to avoid conflicts

    // Historical bars for candlestick preloading
    private readonly ConcurrentDictionary<int, TaskCompletionSource<List<Candlestick>>> _histBarsWaiters = new();
    private readonly ConcurrentDictionary<int, List<Candlestick>> _histBarsBuffers = new();
    private readonly ConcurrentDictionary<int, (string Symbol, string Interval)> _histBarsMetadata = new();

    // Tick stream for live updates
    private readonly Subject<TickData> _tickSubject = new();
    public IObservable<TickData> TickStream => _tickSubject.AsObservable();

    // Streaming historical bars for MACD (keepUpToDate=true)
    private readonly ConcurrentDictionary<int, (string Symbol, string Interval)> _streamingHistReqMetadata = new();
    private readonly Subject<Candlestick> _streamingBarSubject = new();
    public IObservable<Candlestick> StreamingBarStream => _streamingBarSubject.AsObservable();

    // Scanner events
    public event Func<ScannerSnapshot, Task>? SnapshotReceived;

    private class MarketState
    {
        public decimal? LastPrice { get; set; }
        public decimal? Open { get; set; }
        public decimal? High { get; set; }
        public decimal? Low { get; set; }
        public decimal? PrevClose { get; set; }
        public long? Volume { get; set; }
        public long? AverageVolume { get; set; }
    }

    private sealed class HistoricalCacheEntry
    {
        public decimal? PrevClose { get; init; }
        public long? AverageVolume { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; }
    }

    /// <summary>
    /// IBKR often reports US stock market-data volume in lots.
    /// Normalize to shares so scanner/filter thresholds match user expectations.
    /// </summary>
    private static long NormalizeReportedVolume(long rawVolume)
    {
        if (rawVolume <= 0) return rawVolume;
        return checked(rawVolume * US_STOCK_VOLUME_MULTIPLIER);
    }

    private void TouchCallback()
    {
        Interlocked.Exchange(ref _lastCallbackUtcTicks, DateTime.UtcNow.Ticks);
    }

    private DateTime GetLastCallbackUtc()
    {
        var ticks = Interlocked.Read(ref _lastCallbackUtcTicks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    public IbkrGatewayService(ILogger<IbkrGatewayService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    #region Connection Management

    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return;

        await _connectionGate.WaitAsync(ct);
        try
        {
            if (IsConnected) return;
            StopReaderLoop();

            if (_client != null && _client.IsConnected())
            {
                _logger.LogWarning("IBKR socket is open but session is not ready. Reconnecting.");
                SafeDisconnectClient();
            }

            _nextValidId = 0;
            _connected = false;

            var host = _config.GetValue<string>("Ibkr:Host") ?? "127.0.0.1";
            var port = _config.GetValue<int?>("Ibkr:Port") ?? 4002;
            var clientId = _config.GetValue<int?>("Ibkr:ClientId") ?? 7777;

            _logger.LogInformation("Connecting to IBKR at {Host}:{Port} with ClientId {ClientId}",
                host, port, clientId);

            var signal = new EReaderMonitorSignal();
            _signal = signal;

            var client = new EClientSocket(this, signal);
            _client = client;
            client.eConnect(host, port, clientId);

            if (!client.IsConnected())
            {
                throw new InvalidOperationException("IBKR socket connection failed");
            }

            var reader = new EReader(client, signal);
            reader.Start();
            var readerLoopCts = new CancellationTokenSource();
            var readerLoopTask = Task.Run(() => RunReaderLoop(client, reader, signal, readerLoopCts.Token), readerLoopCts.Token);
            lock (_readerLoopLock)
            {
                _readerLoopCts = readerLoopCts;
                _readerLoopTask = readerLoopTask;
            }

            // Wait for nextValidId callback (confirms full API session)
            var connected = await WaitUntilAsync(
                () => client.IsConnected() && _nextValidId > 0,
                TimeSpan.FromSeconds(8),
                ct);

            if (!connected)
            {
                SafeDisconnectClient();
                throw new TimeoutException("Timed out waiting for IBKR nextValidId");
            }

            _connected = true;

            // CRITICAL: Set to DELAYED immediately after connection
            client.reqMarketDataType(3); // 3 = DELAYED
            _logger.LogInformation("Connected to IBKR (nextValidId={Id}, mode=DELAYED)", _nextValidId);
            PublishConnectionState(isConnected: true, "Connected");
            StopReconnectLoop();

            RestoreMarketDataSubscriptions(includeHistorical: false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureConnectedAsync(ct);
            return IsConnected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IBKR reconnect attempt failed");
            return false;
        }
    }

    private void RunReaderLoop(EClientSocket client, EReader reader, EReaderSignal signal, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && client.IsConnected())
            {
                signal.waitForSignal();
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    reader.processMsgs();
                    TouchCallback();
                }
                catch (FormatException ex)
                {
                    _logger.LogError(ex, "IBKR reader parse error. Disconnecting to recover cleanly.");
                    try { client.eDisconnect(); } catch { }
                    MarkDisconnected("Reader parse failure");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "IBKR reader loop error. Continuing.");
                }
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested && !client.IsConnected())
            {
                MarkDisconnected("Socket disconnected");
            }
        }
    }

    private void SafeDisconnectClient()
    {
        StopReaderLoop();
        try
        {
            if (_client?.IsConnected() == true)
            {
                _client.eDisconnect();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while disconnecting IBKR client");
        }
    }

    private void StopReaderLoop()
    {
        CancellationTokenSource? readerCts = null;
        EReaderSignal? signal = null;
        lock (_readerLoopLock)
        {
            readerCts = _readerLoopCts;
            signal = _signal;
            _readerLoopCts = null;
            _readerLoopTask = null;
        }

        if (readerCts == null)
        {
            return;
        }

        try
        {
            readerCts?.Cancel();
        }
        catch { }

        try
        {
            signal?.issueSignal();
        }
        catch { }

        readerCts?.Dispose();
    }

    private void MarkDisconnected(string reason)
    {
        StopReaderLoop();
        _connected = false;
        _nextValidId = 0;

        foreach (var (reqId, waiter) in _scannerWaiters.ToArray())
        {
            waiter.TrySetException(new IOException($"IBKR disconnected: {reason}"));
            CancelScannerQuietTimer(reqId);
        }

        _logger.LogWarning("IBKR connection marked disconnected: {Reason}", reason);
        PublishConnectionState(isConnected: false, reason);
        StartReconnectLoop();
    }

    private void PublishConnectionState(bool isConnected, string reason)
    {
        lock (_connectionStatusLock)
        {
            if (_lastPublishedConnectionStatus.HasValue && _lastPublishedConnectionStatus.Value == isConnected)
            {
                return;
            }

            _lastPublishedConnectionStatus = isConnected;
        }

        try
        {
            ConnectionStateChanged?.Invoke(isConnected, reason);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish connection state change");
        }
    }

    private void StartReconnectLoop()
    {
        if (_disposed || IsConnected)
        {
            return;
        }

        if (_reconnectLoopTask != null && !_reconnectLoopTask.IsCompleted)
        {
            return;
        }

        _reconnectLoopCts?.Cancel();
        _reconnectLoopCts?.Dispose();
        _reconnectLoopCts = new CancellationTokenSource();
        var ct = _reconnectLoopCts.Token;

        _reconnectLoopTask = Task.Run(async () =>
        {
            var delaySeconds = 2;
            while (!ct.IsCancellationRequested && !_disposed && !IsConnected)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                    if (ct.IsCancellationRequested || _disposed || IsConnected)
                    {
                        break;
                    }

                    var reconnected = await TryReconnectAsync(ct);
                    if (reconnected)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Background reconnect attempt failed");
                }

                delaySeconds = Math.Min(delaySeconds * 2, 15);
            }
        }, ct);
    }

    private void StopReconnectLoop()
    {
        var reconnectCts = _reconnectLoopCts;
        _reconnectLoopCts = null;
        try
        {
            reconnectCts?.Cancel();
        }
        catch { }
        finally
        {
            reconnectCts?.Dispose();
        }
    }

    private void RestoreMarketDataSubscriptions(bool includeHistorical = false)
    {
        if (!IsConnected || _client == null)
        {
            return;
        }

        var existingSubscriptions = _idToSymbol.ToArray();
        if (existingSubscriptions.Length == 0)
        {
            return;
        }

        _logger.LogInformation("Restoring {Count} market data subscriptions after reconnect", existingSubscriptions.Length);
        foreach (var (tickerId, symbol) in existingSubscriptions)
        {
            var contract = new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD"
            };

            try
            {
                _client.reqMktData(tickerId, contract, "233", false, false, null);
                if (includeHistorical)
                {
                    RequestHistoricalDataIfNeeded(symbol, contract, AvgVolumeRequestDuration);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore market data subscription for {Symbol} (tickerId={TickerId})", symbol, tickerId);
            }
        }
    }

    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (!condition() && DateTime.UtcNow - start < timeout && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);
        }
        return condition();
    }

    private async Task EnsurePumpHealthyAsync(CancellationToken ct)
    {
        if (!IsConnected)
        {
            return;
        }

        var age = DateTime.UtcNow - GetLastCallbackUtc();
        if (age <= CallbackStaleThreshold)
        {
            return;
        }

        _logger.LogWarning("IBKR callback pump appears stale (last callback {AgeSeconds:F1}s ago). Reconnecting once.", age.TotalSeconds);
        SafeDisconnectClient();
        await EnsureConnectedAsync(ct);
    }

    private int GetNextReqId() => Interlocked.Increment(ref _nextReqId);

    #endregion

    #region IScanner Implementation

    public async Task<IReadOnlyList<ScannerRow>> ScanAsync(CancellationToken ct)
    {
        return await ScanCoreAsync(2, 20, 100000, "stocks", "us stocks", 50, forceRefresh: false, ct);
    }

    public async Task<IReadOnlyList<ScannerRow>> ScanAsync(
        decimal minPrice = 2,
        decimal maxPrice = 20,
        decimal minVol = 100000,
        string product = "stocks",
        string exchange = "us stocks",
        int topN = 50,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        return await ScanCoreAsync(minPrice, maxPrice, minVol, product, exchange, topN, forceRefresh, ct);
    }

    private async Task<IReadOnlyList<ScannerRow>> ScanCoreAsync(
        decimal minPrice,
        decimal maxPrice,
        decimal minVol,
        string product,
        string exchange,
        int topN,
        bool forceRefresh,
        CancellationToken ct)
    {
        await _scannerExecutionGate.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            await EnsurePumpHealthyAsync(ct);

            var cacheKey = BuildScanCacheKey(minPrice, maxPrice, minVol, product, exchange, topN);
            if (!forceRefresh && TryGetFreshScanCache(cacheKey, out var cachedRows))
            {
                _logger.LogInformation("Using cached scanner universe ({Count} rows, age={AgeSeconds:F0}s)", cachedRows.Count, (DateTimeOffset.UtcNow - _lastScanAtUtc).TotalSeconds);
                var cachedSymbols = cachedRows.Select(r => r.Symbol).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                SubscribeToMarketData(cachedSymbols, isFromScanner: true);
                await WaitForInitialMarketDataAsync(cachedSymbols, TimeSpan.FromMilliseconds(400), ct);
                return HydrateScannerRowsFromMarketState(cachedRows);
            }
            else if (forceRefresh)
            {
                _logger.LogInformation("Force-refresh requested; bypassing scanner cache for this call");
            }

            // Cancel any existing scanner before starting new one
            if (_currentScannerId > 0)
            {
                _logger.LogDebug("Cancelling previous scanner reqId={PreviousId}", _currentScannerId);
                try
                {
                    _client.cancelScannerSubscription(_currentScannerId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to cancel previous scanner reqId={PreviousId}", _currentScannerId);
                }
                if (_scannerWaiters.TryRemove(_currentScannerId, out var previousWaiter))
                {
                    previousWaiter.TrySetCanceled();
                }
                _scannerBuffers.TryRemove(_currentScannerId, out _);
                _scannerRequestedRows.TryRemove(_currentScannerId, out _);
                CancelScannerQuietTimer(_currentScannerId);
                _scannerDataCounts.TryRemove(_currentScannerId, out _);
                _scannerDataEndSeen.TryRemove(_currentScannerId, out _);
                _scannerLastErrorCodes.TryRemove(_currentScannerId, out _);
            }

            var requestId = GetNextReqId();
            _currentScannerId = requestId; // Track current scanner for future cancellation
            var tcs = new TaskCompletionSource<List<ScannerRow>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _scannerWaiters[requestId] = tcs;
        _scannerBuffers[requestId] = new List<ScannerRow>();
        _scannerRequestedRows[requestId] = topN;
        _scannerRequestIds[requestId] = true; // Track for Error 162 suppression
        _scannerDataCounts[requestId] = 0;
        _scannerDataEndSeen.TryRemove(requestId, out _);
        _scannerLastErrorCodes.TryRemove(requestId, out _);
        _logger.LogDebug("Registered scanner waiter/buffer reqId={ReqId}, topN={TopN}", requestId, topN);

        var normalizedProduct = string.IsNullOrWhiteSpace(product) ? "stocks" : product.Trim().ToLowerInvariant();
        var (instrument, locationCode) = normalizedProduct switch
        {
            "futures" => ("FUT", "FUT.US"),
            "stocks" => ("STK", IbkrConstants.US_STOCKS_MAJOR),
            "etfs" => ("STK", IbkrConstants.US_STOCKS_MAJOR),
            _ => ("STK", IbkrConstants.US_STOCKS_MAJOR)
        };

        _logger.LogInformation("Scanner request shape: product={Product}, instrument={Instrument}, locationCode={LocationCode}", normalizedProduct, instrument, locationCode);
        var scannerSubscription = new ScannerSubscription
        {
            Instrument = instrument,
            LocationCode = locationCode,
            ScanCode = "TOP_PERC_GAIN",
            NumberOfRows = topN,
            AboveVolume = 100_000
        };

        // Scanner options (empty list - not used)
        var scanOptions = new List<TagValue>();

        // Filter options - price, exchange filters applied at IBKR level via TagValue
        var filterOptions = new List<TagValue>
        {
            new TagValue("priceAbove", minPrice.ToString("F2")),
            new TagValue("priceBelow", maxPrice.ToString("F2")),
            new TagValue("volumeAbove", minVol.ToString("F2"))  // Keep volume filter (even though it's broken)
        };

        // Add exchange filter if specified (not "any" or "us stocks")
        if (!string.IsNullOrWhiteSpace(exchange) &&
            !exchange.Equals("any", StringComparison.OrdinalIgnoreCase) &&
            !exchange.Equals("us stocks", StringComparison.OrdinalIgnoreCase) &&
            instrument == "STK")
        {
            // Map UI values to IBKR exchange codes
            var exchangeCode = exchange.ToUpperInvariant() switch
            {
                "NASDAQ" => "NASDAQ",
                "NYSE" => "NYSE",
                "AMEX" => "AMEX",
                _ => exchange.ToUpperInvariant()
            };
            filterOptions.Add(new TagValue("exchange", exchangeCode));
            _logger.LogInformation("Adding exchange filter: {Exchange}", exchangeCode);
        }
        else if (instrument != "STK")
        {
            _logger.LogDebug("Skipping stock exchange tag filter for non-stock instrument={Instrument}", instrument);
        }

        using var timeoutCts = new CancellationTokenSource(ScannerRequestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            _logger.LogInformation("Starting scanner subscription reqId={RequestId} with filters: price ${MinPrice}-${MaxPrice}, product={Product}, locationCode={LocationCode}, exchange={Exchange}, volume >100k, topN={TopN}",
                requestId, minPrice, maxPrice, product, locationCode, exchange, topN);
            _client.reqScannerSubscription(requestId, scannerSubscription, scanOptions, filterOptions);
            ScheduleScannerSilenceProbe(requestId, instrument, locationCode, exchange, minPrice, maxPrice, minVol, topN);

            var rows = await tcs.Task.WaitAsync(linkedCts.Token);
            _logger.LogInformation("Scanner returned {Count} rows", rows.Count);

            // Cancel only scanner market data subscriptions (10000-19999) to avoid
            // killing manual/watchlist subscriptions during scanner refresh.
            var scannerSubscriptions = _idToSymbol
                .Where(kvp => kvp.Key >= 10000 && kvp.Key < 20000)
                .ToList();

            foreach (var kvp in scannerSubscriptions)
            {
                try
                {
                    _client.cancelMktData(kvp.Key);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to cancel market data tickerId={TickerId} during scanner reset", kvp.Key);
                }
            }
            foreach (var kvp in scannerSubscriptions)
            {
                _idToSymbol.TryRemove(kvp.Key, out _);
            }

            // Remove market state only if the symbol no longer has any active subscription.
            var removedSymbols = scannerSubscriptions
                .Select(kvp => kvp.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in removedSymbols)
            {
                var stillSubscribed = _idToSymbol.Values.Any(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase));
                if (!stillSubscribed)
                {
                    _marketState.TryRemove(symbol, out _);
                }
            }

            // Cancel historical data requests
            foreach (var kvp in _histReqToSymbol.ToList())
            {
                try
                {
                    _client.cancelHistoricalData(kvp.Key);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to cancel historical request reqId={ReqId} during scanner reset", kvp.Key);
                }
                _historicalInFlight.TryRemove(kvp.Value, out _);
            }
            _histReqToSymbol.Clear();
            _histVolumes.Clear();
            _histClosePrices.Clear();
            _histBars.Clear();
            // Cancel any pending bar waiters
            foreach (var kvp in _histBarWaiters.ToList())
            {
                kvp.Value.TrySetCanceled();
            }
            _histBarWaiters.Clear();

            // Subscribe to market data for all scanner results
            var symbols = rows.Select(r => r.Symbol).ToList();
            SubscribeToMarketData(symbols, isFromScanner: true);

            // Initial hydration pass from reqMktData (last/close/volume) so first render
            // is less likely to show all-zero pending values.
            await WaitForInitialMarketDataAsync(symbols, TimeSpan.FromMilliseconds(800), ct);
            var hydratedRows = HydrateScannerRowsFromMarketState(rows);
            UpdateScanCache(cacheKey, hydratedRows);
            return hydratedRows;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Scanner request cancelled by caller");
            throw;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Scanner request timed out");
            var dataCount = _scannerDataCounts.TryGetValue(requestId, out var timeoutDataCount) ? timeoutDataCount : 0;
            var sawDataEnd = _scannerDataEndSeen.ContainsKey(requestId);
            var lastErrorCode = _scannerLastErrorCodes.TryGetValue(requestId, out var timeoutErrorCode) ? timeoutErrorCode : (int?)null;
            _logger.LogWarning(
                "Scanner timeout diagnostics reqId={ReqId}: scannerDataCount={DataCount}, scannerDataEndSeen={SawDataEnd}, lastErrorCode={LastErrorCode}",
                requestId, dataCount, sawDataEnd, lastErrorCode);

            var partialRows = GetScannerBufferSnapshot(requestId);
            if (partialRows.Count > 0)
            {
                var hydratedPartial = HydrateScannerRowsFromMarketState(partialRows);
                _logger.LogWarning(
                    "Scanner timeout for reqId={ReqId}, returning partial result set with {Count} rows",
                    requestId,
                    hydratedPartial.Count);
                UpdateScanCache(cacheKey, hydratedPartial);
                return hydratedPartial;
            }

            if (TryGetScanCacheRegardlessOfAge(cacheKey, out var staleRows) && staleRows.Count > 0)
            {
                var hydratedStale = HydrateScannerRowsFromMarketState(staleRows);
                _logger.LogWarning(
                    "Scanner timeout for reqId={ReqId}, reusing stale scanner cache with {Count} rows",
                    requestId,
                    hydratedStale.Count);
                return hydratedStale;
            }

            _logger.LogWarning("Scanner timeout for reqId={ReqId}, returning empty result set", requestId);
            return Array.Empty<ScannerRow>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scanner request failed for reqId={ReqId}; falling back to partial/stale/empty results", requestId);

            var partialRows = GetScannerBufferSnapshot(requestId);
            if (partialRows.Count > 0)
            {
                var hydratedPartial = HydrateScannerRowsFromMarketState(partialRows);
                _logger.LogWarning(
                    "Scanner failure fallback for reqId={ReqId}, returning partial result set with {Count} rows",
                    requestId,
                    hydratedPartial.Count);
                UpdateScanCache(cacheKey, hydratedPartial);
                return hydratedPartial;
            }

            if (TryGetScanCacheRegardlessOfAge(cacheKey, out var staleRows) && staleRows.Count > 0)
            {
                var hydratedStale = HydrateScannerRowsFromMarketState(staleRows);
                _logger.LogWarning(
                    "Scanner failure fallback for reqId={ReqId}, reusing stale scanner cache with {Count} rows",
                    requestId,
                    hydratedStale.Count);
                return hydratedStale;
            }

            _logger.LogWarning("Scanner failure fallback for reqId={ReqId}, returning empty result set", requestId);
            return Array.Empty<ScannerRow>();
        }
        finally
        {
            try
            {
                _client.cancelScannerSubscription(requestId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cancel scanner subscription reqId={ReqId} during cleanup", requestId);
            }
            _scannerWaiters.TryRemove(requestId, out _);
            _scannerBuffers.TryRemove(requestId, out _);
            _scannerRequestedRows.TryRemove(requestId, out _);
            CancelScannerQuietTimer(requestId);
            _scannerDataCounts.TryRemove(requestId, out _);
            _scannerDataEndSeen.TryRemove(requestId, out _);
            _scannerLastErrorCodes.TryRemove(requestId, out _);
            _scannerRequestIds.TryRemove(requestId, out _); // Clean up Error 162 suppression tracking
            if (_currentScannerId == requestId)
            {
                _currentScannerId = 0; // Reset current scanner ID
            }
            _logger.LogDebug("Removed scanner waiter/buffer reqId={ReqId}", requestId);
        }
        }
        finally
        {
            _scannerExecutionGate.Release();
        }
    }

    private async Task WaitForInitialMarketDataAsync(
        IReadOnlyCollection<string> symbols,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        if (symbols.Count == 0)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < maxWait && !ct.IsCancellationRequested)
        {
            var ready = 0;
            foreach (var symbol in symbols)
            {
                if (_marketState.TryGetValue(symbol, out var state) &&
                    state.LastPrice.HasValue && state.LastPrice.Value > 0 &&
                    state.Volume.HasValue && state.Volume.Value > 0)
                {
                    ready++;
                }
            }

            if (ready == symbols.Count)
            {
                break;
            }

            await Task.Delay(50, ct);
        }
    }

    private List<ScannerRow> GetScannerBufferSnapshot(int reqId)
    {
        if (!_scannerBuffers.TryGetValue(reqId, out var buffer))
        {
            return new List<ScannerRow>();
        }

        lock (buffer)
        {
            return buffer.Select(CloneScannerRow).ToList();
        }
    }

    private void ScheduleScannerQuietCompletion(int reqId)
    {
        if (!_scannerWaiters.ContainsKey(reqId))
        {
            return;
        }

        var timerCts = new CancellationTokenSource();
        if (_scannerQuietTimers.TryGetValue(reqId, out var previousCts))
        {
            _scannerQuietTimers[reqId] = timerCts;
            try
            {
                previousCts.Cancel();
            }
            catch { }
            finally
            {
                previousCts.Dispose();
            }
        }
        else
        {
            _scannerQuietTimers[reqId] = timerCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ScannerQuietPeriod, timerCts.Token);
                TryCompleteScannerFromBuffer(reqId, "quiet-period", requireNonEmpty: true);
            }
            catch (OperationCanceledException)
            {
                // Scanner still receiving updates, ignore.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Scanner quiet-period completion failed for reqId={ReqId}", reqId);
            }
        });
    }

    private bool TryCompleteScannerFromBuffer(int reqId, string reason, bool requireNonEmpty)
    {
        if (!_scannerWaiters.TryGetValue(reqId, out var tcs))
        {
            return false;
        }

        var snapshot = GetScannerBufferSnapshot(reqId);
        if (requireNonEmpty && snapshot.Count == 0)
        {
            return false;
        }

        var completed = tcs.TrySetResult(snapshot);
        if (completed)
        {
            _logger.LogDebug("Completed scanner reqId={ReqId} via {Reason} with {Count} rows", reqId, reason, snapshot.Count);
        }
        return completed;
    }

    private void TryCompleteScannerFromError(int reqId, int errorCode, string errorMsg)
    {
        if (!_scannerWaiters.TryGetValue(reqId, out var tcs))
        {
            return;
        }

        var partialRows = GetScannerBufferSnapshot(reqId);
        if (errorCode is 165 or 162 or 322 or 492 or 365)
        {
            _logger.LogWarning("Completing scanner reqId={ReqId} from scanner error {Code} with {Count} buffered rows", reqId, errorCode, partialRows.Count);
            tcs.TrySetResult(partialRows);
            CancelScannerQuietTimer(reqId);
            return;
        }

        if (partialRows.Count > 0)
        {
            _logger.LogWarning("Scanner reqId={ReqId} got error {Code}; returning partial buffer ({Count} rows)", reqId, errorCode, partialRows.Count);
            tcs.TrySetResult(partialRows);
        }
        else
        {
            tcs.TrySetException(new Exception($"IBKR scanner error {errorCode}: {errorMsg}"));
        }
        CancelScannerQuietTimer(reqId);
    }

    private void CancelScannerQuietTimer(int reqId)
    {
        if (!_scannerQuietTimers.TryRemove(reqId, out var timerCts))
        {
            return;
        }

        try
        {
            timerCts.Cancel();
        }
        catch { }
        finally
        {
            timerCts.Dispose();
        }
    }

    private void ScheduleScannerSilenceProbe(
        int reqId,
        string instrument,
        string locationCode,
        string exchange,
        decimal minPrice,
        decimal maxPrice,
        decimal minVol,
        int topN)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));

            if (!_scannerWaiters.TryGetValue(reqId, out var waiter) || waiter.Task.IsCompleted)
            {
                return;
            }

            var dataCount = _scannerDataCounts.TryGetValue(reqId, out var seenDataCount) ? seenDataCount : 0;
            var sawDataEnd = _scannerDataEndSeen.ContainsKey(reqId);
            var sawError = _scannerLastErrorCodes.TryGetValue(reqId, out var errorCode);

            if (dataCount == 0 && !sawDataEnd && !sawError)
            {
                _logger.LogWarning(
                    "Scanner callbacks still silent after 5s for reqId={ReqId}. Likely IB-side silence or request-shape mismatch. Shape: instrument={Instrument}, locationCode={LocationCode}, exchange={Exchange}, price={MinPrice}-{MaxPrice}, minVol={MinVol}, topN={TopN}",
                    reqId, instrument, locationCode, exchange, minPrice, maxPrice, minVol, topN);
            }
        });
    }

    private static string BuildScanCacheKey(
        decimal minPrice,
        decimal maxPrice,
        decimal minVol,
        string product,
        string exchange,
        int topN)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{product.ToUpperInvariant()}|{exchange.ToUpperInvariant()}|{minPrice:F2}|{maxPrice:F2}|{minVol:F0}|{topN}");
    }

    private bool TryGetFreshScanCache(string cacheKey, out IReadOnlyList<ScannerRow> rows)
    {
        lock (_scanCacheLock)
        {
            var isFresh = _lastScanRows.Count > 0 &&
                          string.Equals(_lastScanCacheKey, cacheKey, StringComparison.Ordinal) &&
                          (DateTimeOffset.UtcNow - _lastScanAtUtc) <= ScannerResultCacheTtl;
            if (isFresh)
            {
                rows = _lastScanRows;
                return true;
            }
        }

        rows = Array.Empty<ScannerRow>();
        return false;
    }

    private bool TryGetScanCacheRegardlessOfAge(string cacheKey, out IReadOnlyList<ScannerRow> rows)
    {
        lock (_scanCacheLock)
        {
            var hasMatchingCache = _lastScanRows.Count > 0 &&
                                   string.Equals(_lastScanCacheKey, cacheKey, StringComparison.Ordinal);
            if (hasMatchingCache)
            {
                rows = _lastScanRows;
                return true;
            }
        }

        rows = Array.Empty<ScannerRow>();
        return false;
    }

    private void UpdateScanCache(string cacheKey, IReadOnlyList<ScannerRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        lock (_scanCacheLock)
        {
            _lastScanCacheKey = cacheKey;
            _lastScanAtUtc = DateTimeOffset.UtcNow;
            _lastScanRows = rows.Select(CloneScannerRow).ToArray();
        }
    }

    private static ScannerRow CloneScannerRow(ScannerRow row)
    {
        return new ScannerRow
        {
            ReqId = row.ReqId,
            Symbol = row.Symbol,
            Company = row.Company,
            LastPrice = row.LastPrice,
            Change = row.Change,
            ChangePct = row.ChangePct,
            Volume = row.Volume,
            AvgVolume = row.AvgVolume,
            RelativeVolume = row.RelativeVolume,
            Float = row.Float,
            High52W = row.High52W,
            Meta = row.Meta is null
                ? null
                : new InstrumentMetadata
                {
                    Symbol = row.Meta.Symbol,
                    Company = row.Meta.Company,
                    Sector = row.Meta.Sector,
                    Exchange = row.Meta.Exchange,
                    Region = row.Meta.Region,
                    Product = row.Meta.Product
                }
        };
    }

    private IReadOnlyList<ScannerRow> HydrateScannerRowsFromMarketState(IReadOnlyList<ScannerRow> rows)
    {
        var enriched = new List<ScannerRow>(rows.Count);

        foreach (var row in rows)
        {
            if (!_marketState.TryGetValue(row.Symbol, out var state))
            {
                enriched.Add(row);
                continue;
            }

            var lastPrice = state.LastPrice.HasValue && state.LastPrice.Value > 0
                ? state.LastPrice.Value
                : row.LastPrice;

            var prevClose = state.PrevClose.HasValue && state.PrevClose.Value > 0
                ? state.PrevClose.Value
                : 0m;

            var volume = state.Volume.HasValue && state.Volume.Value > 0
                ? state.Volume.Value
                : row.Volume;

            var avgVolume = state.AverageVolume.HasValue && state.AverageVolume.Value > 0
                ? state.AverageVolume.Value
                : _averageVolumes.GetValueOrDefault(row.Symbol, row.AvgVolume);

            var change = row.Change;
            var changePct = row.ChangePct;
            if (prevClose > 0 && lastPrice > 0)
            {
                change = lastPrice - prevClose;
                changePct = (change / prevClose) * 100m;
            }

            var relativeVolume = avgVolume > 0
                ? (decimal)VolumeCalculations.CalculateRelativeVolume(volume, avgVolume)
                : row.RelativeVolume;

            enriched.Add(new ScannerRow
            {
                ReqId = row.ReqId,
                Symbol = row.Symbol,
                Company = row.Company,
                LastPrice = lastPrice,
                Change = change,
                ChangePct = changePct,
                RelativeVolume = relativeVolume,
                Volume = volume,
                AvgVolume = avgVolume,
                Float = row.Float,
                High52W = row.High52W,
                Meta = row.Meta
            });
        }

        return enriched;
    }

    public Task CancelScannerAsync()
    {
        if (_currentScannerId > 0)
        {
            _logger.LogInformation("Cancelling current scanner subscription reqId={ScannerId}", _currentScannerId);
            _client.cancelScannerSubscription(_currentScannerId);
            if (_scannerWaiters.TryRemove(_currentScannerId, out var waiter))
            {
                waiter.TrySetCanceled();
            }
            _scannerBuffers.TryRemove(_currentScannerId, out _);
            _scannerRequestedRows.TryRemove(_currentScannerId, out _);
            CancelScannerQuietTimer(_currentScannerId);
            _scannerDataCounts.TryRemove(_currentScannerId, out _);
            _scannerDataEndSeen.TryRemove(_currentScannerId, out _);
            _scannerLastErrorCodes.TryRemove(_currentScannerId, out _);
            _scannerRequestIds.TryRemove(_currentScannerId, out _); // Clean up Error 162 suppression tracking
            _currentScannerId = 0;
        }
        return Task.CompletedTask;
    }

    public async Task StartAsync(
        FilterState filters,
        CancellationToken cancellationToken,
        Guid sessionId,
        Func<TickData, Task> onTick,
        Func<EnrichmentData, Task> onEnrichment)
    {
        await EnsureConnectedAsync(cancellationToken);

        _logger.LogInformation("Starting scanner with session {SessionId}", sessionId);

        var rows = await ScanAsync(cancellationToken);

        // Subscribe to market data for live updates
        SubscribeToMarketData(rows.Select(r => r.Symbol), isFromScanner: true);

        var scannerItems = rows.Select(row => new ScannerItem
        {
            Symbol = row.Symbol,
            Company = row.Company,
            LastPrice = row.LastPrice,
            Change = row.Change,
            ChangePercent = row.ChangePct,
            Volume = row.Volume,
            AverageVolume = row.AvgVolume,
            RelativeVolume = row.RelativeVolume,
            Float = row.Float,
            FiftyTwoWeekHigh = row.High52W,
            Sector = row.Meta?.Sector ?? "Unknown",
            Exchange = row.Meta?.Exchange ?? "Unknown",
            Region = row.Meta?.Region ?? "us",
            Product = row.Meta?.Product ?? "stocks"
        }).ToList();

        var snapshot = new ScannerSnapshot(scannerItems);
        if (SnapshotReceived != null)
        {
            await SnapshotReceived.Invoke(snapshot);
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Stopping scanner");
        // Cancel all active subscriptions
        foreach (var tickerId in _idToSymbol.Keys)
        {
            try { _client.cancelMktData(tickerId); } catch { }
        }
        _idToSymbol.Clear();
        _marketState.Clear();
        return Task.CompletedTask;
    }

    #endregion

    #region IMarketDataService Implementation

    public async Task<IReadOnlyList<SnapshotRow>> GetSnapshotsAsync(UniverseRequest request, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var rows = await ScanAsync(ct);

        // Subscribe to market data
        SubscribeToMarketData(rows.Select(r => r.Symbol), isFromScanner: true);

        // Wait a bit for ticks to arrive
        await Task.Delay(2000, ct);

        // Convert to snapshots
        return rows.Select(row => new SnapshotRow
        {
            Symbol = row.Symbol,
            Company = row.Company,
            LastPrice = row.LastPrice,
            Change = row.Change,
            ChangePercent = row.ChangePct,
            Volume = row.Volume,
            AverageVolume = _averageVolumes.GetValueOrDefault(row.Symbol, 0),
            RelativeVolume = (decimal)VolumeCalculations.CalculateRelativeVolume(row.Volume, _averageVolumes.GetValueOrDefault(row.Symbol, 0)),
            Float = row.Float,
            FiftyTwoWeekHigh = row.High52W,
            Sector = row.Meta?.Sector ?? "Unknown",
            Exchange = row.Meta?.Exchange ?? "Unknown",
            Region = row.Meta?.Region ?? "us",
            Product = row.Meta?.Product ?? "stocks",
            Timestamp = DateTime.UtcNow
        }).ToList();
    }


    #endregion

    #region Market Data Subscription

    /// <summary>
    /// Public method to subscribe to market data for symbols.
    /// Used when symbols are manually added to watchlists or quotes.
    /// </summary>
    public void SubscribeToSymbols(IEnumerable<string> symbols)
    {
        SubscribeToMarketData(symbols);
    }

    private void SubscribeToMarketData(IEnumerable<string> symbols, bool isFromScanner = false)
    {
        // Only subscribe if connected
        if (!IsConnected || _client == null)
        {
            _logger.LogDebug("Skipping market data subscription - IBKR not connected");
            return;
        }

        // Use different ticker ID ranges: scanner uses 10000-19999, manual subscriptions use 20000+
        var tickerId = isFromScanner ? 10000 : _nextManualTickerId;
        var symbolList = symbols.ToList();
        _logger.LogInformation("SubscribeToMarketData: Subscribing to {Count} symbols: {Symbols} (isFromScanner={IsFromScanner}, startingTickerId={TickerId})",
            symbolList.Count, string.Join(", ", symbolList), isFromScanner, tickerId);

        foreach (var symbol in symbolList)
        {
            if (_idToSymbol.Values.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogDebug("SubscribeToMarketData: {Symbol} already subscribed, skipping", symbol);
                continue; // Already subscribed
            }

            // Check if tickerId is already in use and find next available
            while (_idToSymbol.ContainsKey(tickerId))
            {
                tickerId++;
                if (!isFromScanner && tickerId >= 20000)
                {
                    _nextManualTickerId = tickerId; // Update counter for next time
                }
            }

            _idToSymbol[tickerId] = symbol;
            var state = new MarketState();
            _marketState[symbol] = state;
            TryApplyHistoricalCache(symbol, state);

            var contract = new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD"
            };

            // Request streaming market data
            // Note: Close price will come from historical data (most recent bar's close) and from streaming ticks
            try
            {
                // Request RTVolume (233) so cumulative day volume can be read from tickString (48/77).
                _client.reqMktData(tickerId, contract, "233", false, false, null);
                _logger.LogInformation("SubscribeToMarketData: Requested streaming market data for {Symbol} (tickerId={TickerId})", symbol, tickerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubscribeToMarketData: Failed to request market data for {Symbol} (tickerId={TickerId})", symbol, tickerId);
            }

            // Scanner can use a shorter historical duration to populate PrevClose/AverageVolume
            // while avoiding heavy 60D requests.
            var historyDuration = isFromScanner ? ScannerAvgVolumeRequestDuration : AvgVolumeRequestDuration;
            RequestHistoricalDataIfNeeded(symbol, contract, historyDuration);

            tickerId++;
            if (!isFromScanner && tickerId > _nextManualTickerId)
            {
                _nextManualTickerId = tickerId; // Update counter for next time
            }
        }
    }

    private void RequestHistoricalDataIfNeeded(string symbol, Contract contract, string duration, bool force = false)
    {
        if (!force &&
            _historicalCache.TryGetValue(symbol, out var cached) &&
            DateTimeOffset.UtcNow - cached.UpdatedUtc <= HistoricalCacheTtl &&
            ((cached.AverageVolume.HasValue && cached.AverageVolume.Value > 0) ||
             (cached.PrevClose.HasValue && cached.PrevClose.Value > 0)))
        {
            _logger.LogDebug(
                "RequestHistoricalDataIfNeeded: Using cached historical data for {Symbol} (age={AgeSeconds:F0}s)",
                symbol,
                (DateTimeOffset.UtcNow - cached.UpdatedUtc).TotalSeconds);
            return;
        }

        if (!_historicalInFlight.TryAdd(symbol, 0))
        {
            _logger.LogDebug("RequestHistoricalDataIfNeeded: Historical request already in-flight for {Symbol}", symbol);
            return;
        }

        RequestHistoricalData(symbol, contract, duration);
    }

    private void TryApplyHistoricalCache(string symbol, MarketState state)
    {
        if (!_historicalCache.TryGetValue(symbol, out var cached))
        {
            return;
        }

        if (DateTimeOffset.UtcNow - cached.UpdatedUtc > HistoricalCacheTtl)
        {
            return;
        }

        if (cached.AverageVolume.HasValue && cached.AverageVolume.Value > 0)
        {
            state.AverageVolume = cached.AverageVolume.Value;
            _averageVolumes[symbol] = cached.AverageVolume.Value;
        }

        if (cached.PrevClose.HasValue && cached.PrevClose.Value > 0)
        {
            state.PrevClose = cached.PrevClose.Value;
        }
    }

    private void RequestHistoricalData(string symbol, Contract contract, string duration)
    {
        var reqId = GetNextReqId();
        _histReqToSymbol[reqId] = symbol;
        _histVolumes[reqId] = new List<long>();
        _histClosePrices[reqId] = null; // Initialize close price tracking

        try
        {
            _client.reqHistoricalData(
                reqId, contract, "", duration, "1 day", "TRADES", 1, 1, false, null);
            _logger.LogInformation(
                "RequestHistoricalData: Requested {Duration} historical data for {Symbol} (reqId={ReqId}) to compute {LookbackDays}-day avg volume and previous close",
                duration, symbol, reqId, AvgVolumeLookbackTradingDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RequestHistoricalData: Failed to request historical data for {Symbol} (reqId={ReqId})", symbol, reqId);
            // Clean up on failure
            _histReqToSymbol.TryRemove(reqId, out _);
            _histVolumes.TryRemove(reqId, out _);
            _histClosePrices.TryRemove(reqId, out _);
            _historicalInFlight.TryRemove(symbol, out _);
        }
    }

    /// <summary>
    /// Requests historical bars for technical indicator calculations (e.g., RSI).
    /// Returns full bar data (OHLCV) in chronological order (oldest to newest).
    /// </summary>
    /// <param name="symbol">Stock symbol</param>
    /// <param name="days">Number of days of historical data to request (default 30)</param>
    /// <param name="barSize">Bar size setting (default "1 day" for daily bars)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of historical bars in chronological order (oldest to newest)</returns>
    public async Task<IReadOnlyList<Bar>> GetHistoricalBarsForRSIAsync(
        string symbol,
        int days = 30,
        string barSize = "1 day",
        CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        if (!IsConnected || _client == null || !_client.IsConnected())
        {
            throw new InvalidOperationException($"Cannot request historical data: IBKR gateway is not connected");
        }

        var reqId = GetNextReqId();
        var tcs = new TaskCompletionSource<List<Bar>>();
        var bars = new List<Bar>();

        // Store request mapping
        _histReqToSymbol[reqId] = symbol;
        _histBars[reqId] = bars;
        _histBarWaiters[reqId] = tcs;

        var contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        try
        {
            // Request historical data
            _client.reqHistoricalData(
                reqId,
                contract,
                "",                    // endDateTime: empty = current time
                $"{days} D",           // duration: number of days
                barSize,               // barSize: "1 day", "1 min", etc.
                "TRADES",              // whatToShow: trade data
                1,                     // useRTH: regular trading hours only
                1,                     // formatDate: string format
                false,                 // keepUpToDate: snapshot only (not streaming)
                null                    // chartOptions
            );

            _logger.LogInformation("GetHistoricalBarsForRSIAsync: Requested {Days} days of {BarSize} bars for {Symbol} (reqId={ReqId})",
                days, barSize, symbol, reqId);

            // Wait for historicalDataEnd callback with timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // Register cancellation
            linkedCts.Token.Register(() =>
            {
                if (_histBarWaiters.TryRemove(reqId, out var cancelledTcs))
                {
                    cancelledTcs.TrySetCanceled();
                }
            });

            try
            {
                var result = await tcs.Task.WaitAsync(linkedCts.Token);
                _logger.LogInformation("GetHistoricalBarsForRSIAsync: Received {Count} bars for {Symbol}", result.Count, symbol);
                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogWarning("GetHistoricalBarsForRSIAsync: Request timed out for {Symbol}", symbol);
                throw new TimeoutException($"Historical data request timed out for {symbol}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHistoricalBarsForRSIAsync: Error requesting historical data for {Symbol}", symbol);

            // Clean up on error
            _histBarWaiters.TryRemove(reqId, out _);
            _histBars.TryRemove(reqId, out _);
            _histReqToSymbol.TryRemove(reqId, out _);

            throw;
        }
    }

    #endregion

    #region Symbol Search

    /// <summary>
    /// Searches for symbols matching the pattern using IBKR reqMatchingSymbols API.
    /// </summary>
    public async Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(string pattern, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length < 2)
            return Array.Empty<SymbolSearchResult>();

        await EnsureConnectedAsync(ct);

        var reqId = Interlocked.Increment(ref _nextSearchReqId);
        var tcs = new TaskCompletionSource<List<SymbolSearchResult>>();
        _symbolSearchWaiters[reqId] = tcs;
        _symbolSearchBuffers[reqId] = new List<SymbolSearchResult>();

        try
        {
            _client.reqMatchingSymbols(reqId, pattern);
            _logger.LogDebug("Requested symbol search for pattern: {Pattern}, reqId: {ReqId}", pattern, reqId);

            // Register cancellation
            ct.Register(() =>
            {
                if (_symbolSearchWaiters.TryRemove(reqId, out var cancelledTcs))
                {
                    cancelledTcs.TrySetCanceled();
                    _symbolSearchBuffers.Remove(reqId);
                }
            });

            // Wait for results with timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                var results = await tcs.Task.WaitAsync(linkedCts.Token);
                _logger.LogDebug("Symbol search completed for pattern: {Pattern}, found {Count} results", pattern, results.Count);
                return results;
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogWarning("Symbol search timed out for pattern: {Pattern}", pattern);
                if (_symbolSearchWaiters.TryRemove(reqId, out var timeoutTcs))
                {
                    timeoutTcs.TrySetResult(new List<SymbolSearchResult>());
                }
                return Array.Empty<SymbolSearchResult>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during symbol search for pattern: {Pattern}", pattern);
            if (_symbolSearchWaiters.TryRemove(reqId, out var errorTcs))
            {
                errorTcs.TrySetResult(new List<SymbolSearchResult>());
            }
            return Array.Empty<SymbolSearchResult>();
        }
        finally
        {
            _symbolSearchBuffers.Remove(reqId);
        }
    }

    #endregion

    #region Historical Bars for Candlestick Preloading

    /// <summary>
    /// Fetches historical bars for candlestick preloading.
    /// </summary>
    /// <param name="symbol">The symbol to fetch bars for</param>
    /// <param name="barSizeSeconds">Bar size in seconds (15, 30, 60, 300)</param>
    /// <param name="count">Number of bars to fetch</param>
    /// <returns>List of candlesticks in chronological order (oldest first)</returns>
    public async Task<IReadOnlyList<Candlestick>> GetHistoricalBarsAsync(
    string symbol,
    int barSizeSeconds,
    int count,
    CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var reqId = GetNextReqId();
        var tcs = new TaskCompletionSource<List<Candlestick>>();
        _histBarsWaiters[reqId] = tcs;
        _histBarsBuffers[reqId] = new List<Candlestick>();

        var interval = TimeframeMap.ToIntervalKey(barSizeSeconds);
        _histBarsMetadata[reqId] = (symbol, interval);

        var contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        // ----------------------------------------------------
        // FIX: Use only S or D (hours are NOT supported by IB)
        // ----------------------------------------------------
        var durationSeconds = barSizeSeconds * count * 2; // 2× buffer
        string durationStr;

        if (durationSeconds < 86400)
        {
            // Less than 1 day → seconds are REQUIRED
            var secs = Math.Max(300, durationSeconds);  // minimum 5 minutes
            durationStr = $"{secs} S";                  // always valid
        }
        else
        {
            // ≥ 1 day → days are allowed
            var days = Math.Max(2, (int)Math.Ceiling(durationSeconds / 86400.0));
            durationStr = $"{days} D";
        }

        var barSizeStr = TimeframeMap.ToIbBarSize(barSizeSeconds);

        try
        {
            // Explicit UTC timezone — avoids warning 2174
            var endDateTime = DateTime.UtcNow.ToString("yyyyMMdd HH:mm:ss 'UTC'");
            _logger.LogInformation("GetHistoricalBarsAsync: endDateTime={End}", endDateTime);

            _client.reqHistoricalData(
                reqId,
                contract,
                endDateTime,
                durationStr,
                barSizeStr,
                "TRADES",
                0,
                1,
                false,
                null);

            _logger.LogInformation(
                "GetHistoricalBarsAsync: Requested {Count} bars ({BarSize}) {Duration} for {Symbol} (reqId={ReqId})",
                count, barSizeStr, durationStr, symbol, reqId);

            ct.Register(() =>
            {
                if (_histBarsWaiters.TryRemove(reqId, out var cancelledTcs))
                {
                    cancelledTcs.TrySetCanceled();
                    _histBarsBuffers.TryRemove(reqId, out _);
                    _histBarsMetadata.TryRemove(reqId, out _);
                }
            });

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                var results = await tcs.Task.WaitAsync(linkedCts.Token);
                var sorted = results.OrderBy(c => c.Timestamp).TakeLast(count).ToList();
                _logger.LogInformation("GetHistoricalBarsAsync: Received {Count} bars for {Symbol}", sorted.Count, symbol);
                return sorted;
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogWarning("GetHistoricalBarsAsync: TIMEOUT for {Symbol}", symbol);
                return Array.Empty<Candlestick>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHistoricalBarsAsync: Error requesting historical bars for {Symbol}", symbol);
            return Array.Empty<Candlestick>();
        }
        finally
        {
            _histBarsWaiters.TryRemove(reqId, out _);
            _histBarsBuffers.TryRemove(reqId, out _);
            _histBarsMetadata.TryRemove(reqId, out _);
        }
    }

    /// <summary>
    /// Requests streaming historical bars with keepUpToDate=true for real-time bar updates.
    /// Used for MACD calculations to get stable bar close prices instead of tick-by-tick data.
    /// </summary>
    public void RequestStreamingHistoricalBars(string symbol, int barSizeSeconds, int days = 1)
    {
        if (!IsConnected || _client == null)
        {
            _logger.LogDebug("Skipping streaming historical bars request - IBKR not connected");
            return;
        }

        var reqId = GetNextReqId();

        // Convert barSizeSeconds to IBKR format
        string barSize = TimeframeMap.ToIbBarSize(barSizeSeconds);

        var interval = TimeframeMap.ToIntervalKey(barSizeSeconds);
        _streamingHistReqMetadata[reqId] = (symbol, interval);

        var contract = new Contract
        {
            Symbol = symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        try
        {
            _client.reqHistoricalData(
                reqId,
                contract,
                "",                    // endDateTime: empty = current time
                $"{days} D",           // duration: number of days
                barSize,               // barSize: "5 secs", "1 min", etc.
                "TRADES",              // whatToShow: trade data
                0,                     // useRTH: include extended hours (pre/post) so keepUpToDate can stream outside RTH
                1,                     // formatDate: string format
                true,                  // keepUpToDate: TRUE = streaming updates via historicalDataUpdate
                null                   // chartOptions
            );
            _logger.LogInformation("Requested streaming historical bars for {Symbol} (reqId={ReqId}, barSize={BarSize}, days={Days})",
                symbol, reqId, barSize, days);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to request streaming historical bars for {Symbol}", symbol);
            _streamingHistReqMetadata.TryRemove(reqId, out _);
        }
    }

    /// <summary>
    /// Cancels streaming historical bars for a symbol.
    /// </summary>
    public void CancelStreamingHistoricalBars(string symbol)
    {
        var reqId = _streamingHistReqMetadata.FirstOrDefault(kvp => kvp.Value.Symbol == symbol).Key;
        if (reqId != 0)
        {
            _client?.cancelHistoricalData(reqId);
            _streamingHistReqMetadata.TryRemove(reqId, out _);
            _logger.LogInformation("Cancelled streaming historical bars for {Symbol} (reqId={ReqId})", symbol, reqId);
        }
    }

    #endregion

    #region EWrapper Callbacks

    public void nextValidId(int orderId)
    {
        TouchCallback();
        _nextValidId = orderId;
        _connected = _client != null && _client.IsConnected() && orderId > 0;
        _logger.LogInformation("nextValidId={OrderId}", orderId);
    }

    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr)
    {
        TouchCallback();
        try
        {
            if (!_scannerBuffers.TryGetValue(reqId, out var buffer))
            {
                _logger.LogWarning("scannerData: No buffer found for reqId={ReqId}", reqId);
                return;
            }

            var row = new ScannerRow
            {
                ReqId = reqId,
                Symbol = contractDetails.Contract.Symbol,
                Company = contractDetails.LongName ?? contractDetails.Contract.Symbol,
                LastPrice = 0,
                Change = 0,
                ChangePct = 0,
                Volume = 0,
                AvgVolume = 0,
                RelativeVolume = 0,
                Float = 0,
                High52W = 0,
                Meta = new InstrumentMetadata
                {
                    Symbol = contractDetails.Contract.Symbol,
                    Company = contractDetails.LongName ?? contractDetails.Contract.Symbol,
                    Sector = contractDetails.Industry ?? "Unknown",
                    Exchange = contractDetails.Contract.Exchange ?? "Unknown",
                    Region = "United States",
                    Product = "Stocks"
                }
            };

            int bufferCount;
            lock (buffer)
            {
                buffer.Add(row);
                bufferCount = buffer.Count;
            }

            _logger.LogDebug("scannerData: reqId={ReqId}, rank={Rank}, symbol={Symbol}, bufferSize={BufferSize}",
                reqId, rank, row.Symbol, bufferCount);
            _scannerDataCounts.AddOrUpdate(reqId, 1, (_, current) => current + 1);

            ScheduleScannerQuietCompletion(reqId);

            if (_scannerRequestedRows.TryGetValue(reqId, out var topN) && topN > 0 && bufferCount >= topN)
            {
                TryCompleteScannerFromBuffer(reqId, "topN-reached", requireNonEmpty: true);
                CancelScannerQuietTimer(reqId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in scannerData callback");
            if (_scannerWaiters.TryGetValue(reqId, out var tcs))
            {
                tcs.TrySetException(ex);
                CancelScannerQuietTimer(reqId);
            }
        }
    }

    public void scannerDataEnd(int reqId)
    {
        TouchCallback();
        try
        {
            _logger.LogDebug("scannerDataEnd received for reqId={ReqId}", reqId);
            _scannerDataEndSeen[reqId] = 1;
            var completed = TryCompleteScannerFromBuffer(reqId, "scannerDataEnd", requireNonEmpty: false);
            CancelScannerQuietTimer(reqId);
            if (!completed)
            {
                _logger.LogWarning("scannerDataEnd: waiter not found or already completed for reqId={ReqId}", reqId);
            }
            if (_currentScannerId == reqId)
            {
                _currentScannerId = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in scannerDataEnd callback");
            if (_scannerWaiters.TryGetValue(reqId, out var tcs))
            {
                tcs.TrySetException(ex);
                CancelScannerQuietTimer(reqId);
            }
        }
    }

    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        TouchCallback();
        if (!_idToSymbol.TryGetValue(tickerId, out var symbol)) return;
        if (!_marketState.TryGetValue(symbol, out var state)) return;

        switch (field)
        {
            case TICK_LAST:
            case TICK_DELAYED_LAST:
                state.LastPrice = (decimal)price;
                break;
            case TICK_OPEN:
            case TICK_DELAYED_OPEN:
                state.Open = (decimal)price;
                break;
            case TICK_HIGH:
            case TICK_DELAYED_HIGH:
                state.High = (decimal)price;
                break;
            case TICK_LOW:
            case TICK_DELAYED_LOW:
                state.Low = (decimal)price;
                break;
            case TICK_CLOSE:
            case TICK_DELAYED_CLOSE:
                state.PrevClose = (decimal)price;
                break;
            default:
                return;
        }

        EmitTickUpdate(symbol, state);
    }

    public void tickSize(int tickerId, int field, decimal size)
    {
        TouchCallback();
        if (field != TICK_VOLUME && field != TICK_DELAYED_VOLUME) return;

        if (!_idToSymbol.TryGetValue(tickerId, out var symbol)) return;
        if (!_marketState.TryGetValue(symbol, out var state)) return;

        var normalizedVolume = NormalizeReportedVolume((long)size);
        state.Volume = normalizedVolume;
        _logger.LogDebug("Volume update: {Symbol} raw={RawVolume:N0} normalized={NormalizedVolume:N0}", symbol, (long)size, normalizedVolume);
        EmitTickUpdate(symbol, state);
    }

    public void historicalData(int reqId, Bar bar)
    {
        TouchCallback();
        // Check if this is for candlestick preloading
        if (_histBarsBuffers.TryGetValue(reqId, out var candleBuffer) &&
            _histBarsMetadata.TryGetValue(reqId, out var metadata))
        {
            // Parse timestamp from bar.Time (format: "yyyyMMdd HH:mm:ss" or "yyyyMMdd")
            // IBKR sends timestamps in Eastern Time (market time), so parse as ET and convert to UTC
            // Clean and correct timestamp parsing (IBKR sends UTC-compatible timestamps)
            // IBKR historical timestamps ARE ALWAYS in Eastern Time
            DateTime timestamp;

            string[] formats =
                        {
                "yyyyMMdd  HH:mm:ss",
                "yyyyMMdd HH:mm:ss",
                "yyyyMMdd"
            };

            // IBKR sometimes sends timestamps in the client machine's LOCAL TIME.
            // Convert parsed time from LOCAL → UTC.

            if (DateTime.TryParseExact(
                    bar.Time,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var localTime))
            {
                // Treat as Local Time
                var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Local);

                // Convert Local → UTC
                timestamp = unspecified.ToUniversalTime();
            }
            else
            {
                _logger.LogWarning("Could not parse timestamp: {Time}", bar.Time);
                return;
            }


            var candle = new Candlestick(
                Symbol: metadata.Symbol,
                Open: (decimal)bar.Open,
                High: (decimal)bar.High,
                Low: (decimal)bar.Low,
                Close: (decimal)bar.Close,
                Volume: bar.Volume,
                Timestamp: timestamp,
                Interval: metadata.Interval
            );

            candleBuffer.Add(candle);
            _logger.LogDebug("historicalData: RAW={Raw} ET → UTC={Utc}", bar.Time, timestamp);
            return; // Don't process as regular historical data
        }

        // Check if this is a streaming historical data request (initial bars)
        if (_streamingHistReqMetadata.TryGetValue(reqId, out var streamingMetadata))
        {
            // This is the initial historical data for streaming request
            // Parse and emit as streaming bar (same as historicalDataUpdate)
            DateTime timestamp;
            string[] formats = {
                "yyyyMMdd  HH:mm:ss",
                "yyyyMMdd HH:mm:ss",
                "yyyyMMdd"
            };

            if (DateTime.TryParseExact(
                    bar.Time,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var localTime))
            {
                var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Local);
                timestamp = unspecified.ToUniversalTime();

                var candle = new Candlestick(
                    Symbol: streamingMetadata.Symbol,
                    Open: (decimal)bar.Open,
                    High: (decimal)bar.High,
                    Low: (decimal)bar.Low,
                    Close: (decimal)bar.Close,
                    Volume: bar.Volume,
                    Timestamp: timestamp,
                    Interval: streamingMetadata.Interval
                );

                // Emit as streaming bar update
                _streamingBarSubject.OnNext(candle);
                _logger.LogInformation("historicalData (streaming): {Symbol} - O={Open}, H={High}, L={Low}, C={Close}, V={Volume}, T={Time:o}",
                    streamingMetadata.Symbol, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, timestamp);
            }
            return; // Don't process as regular historical data
        }

        // Original logic for average volume / prev close
        if (!_histReqToSymbol.TryGetValue(reqId, out var symbol))
        {
            _logger.LogWarning("historicalData: Received bar for unknown reqId={ReqId}", reqId);
            return;
        }

        // Track full bars for RSI/technical indicator calculations
        if (_histBars.TryGetValue(reqId, out var bars))
        {
            bars.Add(bar);
        }

        if (_histVolumes.TryGetValue(reqId, out var volumes) && bar.Volume > 0)
        {
            volumes.Add(NormalizeReportedVolume(bar.Volume));
        }

        // Track the most recent close price (historical data comes in reverse chronological order, so first bar is most recent)
        if (_histClosePrices.TryGetValue(reqId, out var currentClose) && !currentClose.HasValue)
        {
            // Store the first (most recent) bar's close price as previous close
            _histClosePrices[reqId] = bar.Close;
            _logger.LogInformation("historicalData: {Symbol} (reqId={ReqId}) - Stored close price {Close} from historical bar (Time={Time}, Open={Open}, High={High}, Low={Low}, Close={Close}, Volume={Volume})",
                symbol, reqId, bar.Close, bar.Time, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        }
        else
        {
            _logger.LogDebug("historicalData: {Symbol} (reqId={ReqId}) - Received additional bar (Time={Time}, Close={Close}, Volume={Volume})",
                symbol, reqId, bar.Time, bar.Close, bar.Volume);
        }
    }

    public void historicalDataEnd(int reqId, string startDate, string endDate)
    {
        TouchCallback();
        // Check if this is for candlestick preloading
        if (_histBarsWaiters.TryGetValue(reqId, out var candleTcs) &&
            _histBarsBuffers.TryGetValue(reqId, out var candleBuffer))
        {
            _logger.LogInformation("historicalDataEnd: Candlestick preload completed for reqId={ReqId}, received {Count} bars", reqId, candleBuffer.Count);
            candleTcs.TrySetResult(candleBuffer);
            return;
        }

        // Check if this is a streaming historical data request
        // For streaming requests, historicalDataEnd just means initial historical data is complete
        // Streaming updates continue via historicalDataUpdate, so don't treat this as the end
        if (_streamingHistReqMetadata.TryGetValue(reqId, out var streamingMetadata))
        {
            _logger.LogInformation("historicalDataEnd (streaming): {Symbol} (reqId={ReqId}) - Initial historical data complete, streaming updates will continue via historicalDataUpdate (startDate={StartDate}, endDate={EndDate})",
                streamingMetadata.Symbol, reqId, startDate, endDate);
            // Don't remove from _streamingHistReqMetadata - keep it active for historicalDataUpdate callbacks
            return; // Don't process as regular historical data end
        }

        // Original logic for regular historical data requests
        if (!_histReqToSymbol.TryGetValue(reqId, out var symbol))
        {
            _logger.LogWarning("historicalDataEnd: Received end for unknown reqId={ReqId}", reqId);
            return;
        }

        _logger.LogInformation("historicalDataEnd: {Symbol} (reqId={ReqId}) - Historical data request completed (startDate={StartDate}, endDate={EndDate})",
            symbol, reqId, startDate, endDate);

        if (_histVolumes.TryGetValue(reqId, out var volumes))
        {
            if (volumes.Count > 0)
            {
                // IBKR daily bars arrive newest-first for this request; use the most recent N bars.
                var lookbackCount = Math.Min(AvgVolumeLookbackTradingDays, volumes.Count);
                var avgVolume = (long)volumes.Take(lookbackCount).Average();
                _averageVolumes[symbol] = avgVolume;

                if (_marketState.TryGetValue(symbol, out var state))
                {
                    state.AverageVolume = avgVolume;

                    // Set previous close from historical data if available
                    if (_histClosePrices.TryGetValue(reqId, out var closePrice) && closePrice.HasValue)
                    {
                        state.PrevClose = (decimal)closePrice.Value;
                        _logger.LogInformation(
                            "historicalDataEnd: {Symbol} - Setting PrevClose={PrevClose} from historical data, avgVolume({LookbackDays}D)={AvgVolume:N0} (used {UsedBarCount}/{TotalBarCount} bars)",
                            symbol, closePrice.Value, AvgVolumeLookbackTradingDays, avgVolume, lookbackCount, volumes.Count);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "historicalDataEnd: {Symbol} - No close price in historical data (used {UsedBarCount}/{TotalBarCount} bars, avgVolume({LookbackDays}D)={AvgVolume:N0})",
                            symbol, lookbackCount, volumes.Count, AvgVolumeLookbackTradingDays, avgVolume);
                    }

                    EmitTickUpdate(symbol, state);
                }
                else
                {
                    _logger.LogWarning("historicalDataEnd: {Symbol} - Completed but no market state found (symbol may have been removed)", symbol);
                }
            }
            else
            {
                _logger.LogWarning("historicalDataEnd: {Symbol} - Completed but no volume data received (no bars returned)", symbol);
            }
        }
        else
        {
            _logger.LogWarning("historicalDataEnd: {Symbol} - Completed but no volume tracking found for reqId={ReqId}", symbol, reqId);
        }

        // Complete bar waiters if any (for RSI/technical indicator requests)
        if (_histBarWaiters.TryRemove(reqId, out var barTcs))
        {
            if (_histBars.TryRemove(reqId, out var completedBars))
            {
                // Historical data comes in reverse chronological order (newest first), reverse to get chronological order
                completedBars.Reverse();
                barTcs.TrySetResult(completedBars);
                _logger.LogInformation("historicalDataEnd: Completed bar request for reqId={ReqId}, returned {Count} bars", reqId, completedBars.Count);
            }
            else
            {
                // No bars were collected (empty result or error), complete with empty list
                _logger.LogWarning("historicalDataEnd: No bars collected for reqId={ReqId}, completing with empty list", reqId);
                barTcs.TrySetResult(new List<Bar>());
            }
        }

        _histReqToSymbol.TryRemove(reqId, out _);
        _histVolumes.TryRemove(reqId, out _);
        _histClosePrices.TryRemove(reqId, out _);
        _historicalInFlight.TryRemove(symbol, out _);

        if (_marketState.TryGetValue(symbol, out var latestState))
        {
            _historicalCache[symbol] = new HistoricalCacheEntry
            {
                PrevClose = latestState.PrevClose,
                AverageVolume = latestState.AverageVolume,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private void EmitTickUpdate(string symbol, MarketState state)
    {
        try
        {
            var change = state.LastPrice.HasValue && state.PrevClose.HasValue
                ? state.LastPrice.Value - state.PrevClose.Value
                : 0m;

            var changePct = state.PrevClose.HasValue && state.PrevClose.Value > 0 && state.LastPrice.HasValue
                ? (state.LastPrice.Value - state.PrevClose.Value) / state.PrevClose.Value * 100
                : 0m;

            var relativeVolume = state.Volume.HasValue && state.AverageVolume.HasValue && state.AverageVolume.Value > 0
                ? (decimal)state.Volume.Value / state.AverageVolume.Value
                : 1m;

            var tickData = new TickData(
                Symbol: symbol,
                SessionId: Guid.Empty,
                LastPrice: (double?)state.LastPrice,
                ClosePrice: (double?)state.PrevClose,
                Volume: state.Volume,
                FiftyTwoWeekHigh: null,
                Timestamp: TimestampUtils.ConvertUtcNowToMarketTime(),
                Bid: null,
                Ask: null,
                High: (double?)state.High,
                Low: (double?)state.Low,
                Open: (double?)state.Open,
                PreviousClose: (double?)state.PrevClose,
                AverageVolume: state.AverageVolume,
                RelativeVolume: relativeVolume,
                Change: change,
                ChangePercent: changePct
            );

            _tickSubject.OnNext(tickData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error emitting tick update for {Symbol}", symbol);
        }
    }

    #endregion

    #region Minimal EWrapper Implementation

    public void error(Exception e)
    {
        TouchCallback();
        _logger.LogError(e, "IBKR Error");
    }
    public void error(string str)
    {
        TouchCallback();
        _logger.LogError("IBKR Error: {Message}", str);
    }

    public void error(int id, int errorCode, string errorMsg)
    {
        TouchCallback();
        _logger.LogDebug("IBKR error callback: reqId={ReqId}, code={Code}, msg={Message}", id, errorCode, errorMsg);
        if (_scannerWaiters.ContainsKey(id))
        {
            _scannerLastErrorCodes[id] = errorCode;
        }

        if (errorCode is 504 or 1100 or 1300 or 2110)
        {
            MarkDisconnected($"IBKR transport error {errorCode}: {errorMsg}");
        }
        else if (errorCode is 1101 or 1102)
        {
            _connected = _client != null && _client.IsConnected() && _nextValidId > 0;
            if (_connected && errorCode == 1101)
            {
                _logger.LogInformation("IBKR data lost/recovered notification ({Code}), restoring subscriptions", errorCode);
                RestoreMarketDataSubscriptions(includeHistorical: false);
            }
        }

        // Log all errors appropriately
        if (errorCode == 492 || errorCode == 162)
        {
            _logger.LogWarning("IBKR Scanner Error {Code}: {Message}. This usually indicates region/exchange mismatch or missing subscriptions.", errorCode, errorMsg);
        }
        else if (errorCode == 322)
        {
            _logger.LogWarning("IBKR Scanner Error {Code} for reqId {Id}: {Message}. This indicates scanner slot exhaustion; request completed without throwing.", errorCode, id, errorMsg);
        }
        else if (errorCode == 365)
        {
            // "No scanner subscription found for ticker id" can occur if the scanner ended before a cancel call.
            _logger.LogWarning("IBKR Scanner Warning {Code} for reqId {Id}: {Message}", errorCode, id, errorMsg);
        }
        else if (errorCode == 165)
        {
            _logger.LogWarning("IBKR Error 165 (Session Conflict): {Message}. Scanner will return empty results. This is expected if another TWS/Gateway instance is running.", errorMsg);
        }
        else if (errorCode == 2176)
        {
            // Error 2176 is a warning about fractional share size rules - not a fatal error
            // This is just informational and shouldn't stop historical data requests
            _logger.LogWarning("IBKR Warning {Code} for reqId {Id}: {Message}. This is informational and does not affect data retrieval.", errorCode, id, errorMsg);
            // Don't treat this as a fatal error - let the request continue
            return;
        }
        else if (errorCode is 2104 or 2106 or 2158)
        {
            _logger.LogInformation("IBKR Status {Code}: {Message}", errorCode, errorMsg);
            return;
        }
        else
        {
            _logger.LogError("IBKR Error {Code} for reqId {Id}: {Message}", errorCode, id, errorMsg);
        }

        // Handle historical data request errors (for RSI/technical indicators)
        // Only fail on actual errors, not warnings like 2176
        if (_histBarWaiters.TryRemove(id, out var histBarTcs))
        {
            var symbol = _histReqToSymbol.GetValueOrDefault(id, "unknown");
            _logger.LogWarning("Historical data request failed for {Symbol} (reqId={ReqId}, errorCode={ErrorCode}): {ErrorMsg}",
                symbol, id, errorCode, errorMsg);

            // Clean up tracking dictionaries
            _histBars.TryRemove(id, out _);
            _histReqToSymbol.TryRemove(id, out _);
            _histVolumes.TryRemove(id, out _);
            _histClosePrices.TryRemove(id, out _);
            if (!string.Equals(symbol, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                _historicalInFlight.TryRemove(symbol, out _);
            }

            // Complete with exception so the caller knows the request failed
            histBarTcs.TrySetException(new Exception($"IBKR Error {errorCode}: {errorMsg}"));
        }

        // Handle scanner-specific errors
        if (_scannerWaiters.ContainsKey(id))
        {
            if (errorCode is not 2104 and not 2106 and not 2158 and not 2176)
            {
                // Always complete scanner requests on non-status errors so callers never hang.
                TryCompleteScannerFromError(id, errorCode, errorMsg);
            }
        }

        if (_histReqToSymbol.TryGetValue(id, out var histSymbol))
        {
            _historicalInFlight.TryRemove(histSymbol, out _);
        }
    }

    public void connectAck()
    {
        TouchCallback();
        _logger.LogInformation("IBKR connection acknowledged");
    }
    public void connectionClosed()
    {
        TouchCallback();
        MarkDisconnected("connectionClosed callback");
    }
    public void currentTime(long time) { }
    public void tickOptionComputation(int tickerId, int field, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickGeneric(int tickerId, int field, double value) { }
    public void tickString(int tickerId, int field, string value)
    {
        TouchCallback();
        if (field != TICK_RT_VOLUME && field != TICK_DELAYED_RT_VOLUME) return;
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!_idToSymbol.TryGetValue(tickerId, out var symbol)) return;
        if (!_marketState.TryGetValue(symbol, out var state)) return;

        // RTVolume format:
        // lastPrice;lastSize;lastTime;totalVolume;vwap;singleTradeFlag
        var parts = value.Split(';');
        if (parts.Length < 4) return;

        if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalVolume))
            return;

        if (totalVolume <= 0) return;

        var normalizedVolume = NormalizeReportedVolume(totalVolume);
        state.Volume = normalizedVolume;
        _logger.LogDebug("RTVolume update: {Symbol} raw={RawVolume:N0} normalized={NormalizedVolume:N0}", symbol, totalVolume, normalizedVolume);
        EmitTickUpdate(symbol, state);
    }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
    public void tickSize(int tickerId, int field, int size)
    {
        // Delegate to decimal overload
        tickSize(tickerId, field, (decimal)size);
    }
    public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealisedPNL, double realisedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountDownloadEnd(string account) { }
    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void bondContractDetails(int reqId, ContractDetails contractDetails) { }
    public void contractDetailsEnd(int reqId) { }
    public void execDetails(int reqId, Contract contract, Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void managedAccounts(string accountsList) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void historicalDataUpdate(int reqId, Bar bar)
    {
        TouchCallback();
        // Check if this is a streaming historical data request
        if (!_streamingHistReqMetadata.TryGetValue(reqId, out var metadata))
        {
            // Not a streaming request, ignore
            return;
        }

        // Parse timestamp from bar.Time (same logic as historicalData callback)
        DateTime timestamp;
        string[] formats = {
            "yyyyMMdd  HH:mm:ss",
            "yyyyMMdd HH:mm:ss",
            "yyyyMMdd"
        };

        if (DateTime.TryParseExact(
                bar.Time,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime))
        {
            var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Local);
            timestamp = unspecified.ToUniversalTime();
        }
        else
        {
            _logger.LogWarning("Could not parse timestamp in historicalDataUpdate: {Time}", bar.Time);
            return;
        }

        // Create Candlestick from bar data
        var candle = new Candlestick(
            Symbol: metadata.Symbol,
            Open: (decimal)bar.Open,
            High: (decimal)bar.High,
            Low: (decimal)bar.Low,
            Close: (decimal)bar.Close,
            Volume: bar.Volume,
            Timestamp: timestamp,
            Interval: metadata.Interval
        );

        // Emit the streaming bar update
        _streamingBarSubject.OnNext(candle);
        _logger.LogInformation("historicalDataUpdate: {Symbol} - O={Open}, H={High}, L={Low}, C={Close}, V={Volume}, T={Time:o}",
            metadata.Symbol, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, timestamp);
    }
    public void scannerParameters(string xml) { }
    public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double WAP, int count) { }
    public void fundamentalData(int reqId, string data) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
    public void tickSnapshotEnd(int tickerId) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void position(string account, Contract contract, double pos, double avgCost) { }
    public void positionEnd() { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
    public void accountSummaryEnd(int reqId) { }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
    public void positionMultiEnd(int reqId) { }
    public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int reqId) { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions)
    {
        TouchCallback();
        try
        {
            if (_symbolSearchWaiters.TryGetValue(reqId, out var tcs))
            {
                var results = contractDescriptions
                    .Select(cd => new SymbolSearchResult
                    {
                        Symbol = cd.Contract.Symbol,
                        Company = cd.DerivativeSecTypes?.FirstOrDefault() ?? cd.Contract.Symbol, // Use symbol as fallback
                        Exchange = cd.Contract.Exchange ?? "SMART",
                        SecType = cd.Contract.SecType ?? "STK"
                    })
                    .DistinctBy(r => r.Symbol, StringComparer.OrdinalIgnoreCase)
                    .Take(10) // Limit to 10 results
                    .ToList();

                _symbolSearchBuffers[reqId].AddRange(results);
                tcs.TrySetResult(_symbolSearchBuffers[reqId]);
                _logger.LogDebug("Received {Count} symbol search results for reqId: {ReqId}", results.Count, reqId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing symbol samples for reqId: {ReqId}", reqId);
            if (_symbolSearchWaiters.TryGetValue(reqId, out var errorTcs))
            {
                errorTcs.TrySetResult(new List<SymbolSearchResult>());
            }
        }
    }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
    public void completedOrder(Contract contract, Order order, OrderState orderState) { }
    public void completedOrdersEnd() { }
    public void replaceFAEnd(int reqId, string text) { }
    public void wshMetaData(int reqId, string dataJson) { }
    public void wshEventData(int reqId, string dataJson) { }
    public void historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone, object[] sessions) { }
    public void userInfo(int reqId, string whiteBrandingId) { }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopReaderLoop();
        try
        {
            if (_client?.IsConnected() == true)
            {
                _client.eDisconnect();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting from IBKR");
        }
        finally
        {
            MarkDisconnected("Dispose");
        }

        _tickSubject.OnCompleted();
        _tickSubject.Dispose();

        try
        {
            _reconnectLoopCts?.Cancel();
            _reconnectLoopCts?.Dispose();
        }
        catch { }
    }
}

