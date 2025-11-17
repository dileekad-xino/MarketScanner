using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Implementation of symbol search service with dummy data fallback and IBKR gateway support.
/// </summary>
public class SymbolSearchService : ISymbolSearchService, IDisposable
{
    private readonly IbkrGatewayService? _ibkrService;
    private readonly ILogger<SymbolSearchService> _logger;
    private readonly Dictionary<string, List<SymbolSearchResult>> _dummyData;
    private bool _disposed;
    private bool? _isConnected; // Lazy-cached connection status (null = not checked yet)
    private readonly object _connectionCheckLock = new object();
    private Task? _initializationTask;
    
    public bool IsConnected
    {
        get
        {
            // Lazy check - only check once, cache the result
            if (_isConnected.HasValue)
                return _isConnected.Value;
                
            lock (_connectionCheckLock)
            {
                // Double-check after acquiring lock
                if (_isConnected.HasValue)
                    return _isConnected.Value;
                    
                _isConnected = CheckInitialConnection();
                
                if (_isConnected.Value)
                {
                    _logger.LogInformation("Symbol search service: IBKR gateway connection detected - will use real data");
                }
                else
                {
                    _logger.LogInformation("Symbol search service: No IBKR gateway connection - will use dummy data");
                }
                
                return _isConnected.Value;
            }
        }
    }
    
    public SymbolSearchService(
        IbkrGatewayService? ibkrService,
        ILogger<SymbolSearchService> logger)
    {
        _ibkrService = ibkrService;
        _logger = logger;
        _dummyData = InitializeDummyData();
        // Connection check will happen in InitializeAsync after connection attempt
    }
    
    /// <summary>
    /// Initializes the service by checking connection status once at application startup.
    /// Should be called after the initial IBKR connection attempt.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Prevent multiple initialization attempts
        if (_initializationTask != null)
        {
            await _initializationTask;
            return;
        }
        
        _initializationTask = Task.Run(async () =>
        {
            // Wait a bit for connection attempt to complete (if it's happening)
            await Task.Delay(TimeSpan.FromSeconds(6));
            
            // Check connection once and cache the result
            lock (_connectionCheckLock)
            {
                if (!_isConnected.HasValue)
                {
                    _isConnected = CheckInitialConnection();
                    
                    if (_isConnected.Value)
                    {
                        _logger.LogInformation("Symbol search service: IBKR gateway connection detected - will use real data");
                    }
                    else
                    {
                        _logger.LogInformation("Symbol search service: No IBKR gateway connection - will use dummy data");
                    }
                }
            }
        });
        
        await _initializationTask;
    }
    
    private bool CheckInitialConnection()
    {
        try
        {
            // Check if IBKR service is available and actually connected
            // This check happens once (lazy initialization)
            return _ibkrService != null && _ibkrService.IsConnected;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check IBKR connection status");
            return false;
        }
    }
    
    private Dictionary<string, List<SymbolSearchResult>> InitializeDummyData()
    {
        var result = new Dictionary<string, List<SymbolSearchResult>>(StringComparer.OrdinalIgnoreCase);
        
        // Load all symbols from PlaybackFallback.DefaultSymbols() to match the symbol universe used by the scanner
        var uniqueSymbols = new HashSet<string>(PlaybackFallback.DefaultSymbols(), StringComparer.OrdinalIgnoreCase);
        
        _logger.LogInformation("Loaded {Count} symbols from PlaybackFallback.DefaultSymbols()", uniqueSymbols.Count);
        
        // Log sample of symbols (first 30)
        var sampleSymbols = uniqueSymbols.Take(30).ToList();
        _logger.LogInformation("Sample symbols in search index: {Symbols}", string.Join(", ", sampleSymbols));
        
        // Build search index - for any 2+ character query, return matching symbols
        foreach (var symbol in uniqueSymbols)
        {
            // Index by symbol prefixes (2+ characters)
            for (int len = 2; len <= symbol.Length; len++)
            {
                var key = symbol.Substring(0, len).ToUpperInvariant();
                if (!result.ContainsKey(key))
                    result[key] = new List<SymbolSearchResult>();
                    
                if (!result[key].Any(r => r.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
                {
                    result[key].Add(new SymbolSearchResult
                    {
                        Symbol = symbol,
                        Company = symbol, // Use symbol as company name
                        Exchange = "SMART",
                        SecType = "STK"
                    });
                }
            }
        }
        
        _logger.LogInformation("Built search index with {Count} prefix keys", result.Count);
        
        return result;
    }
    
    public async Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Array.Empty<SymbolSearchResult>();
            
        var upperQuery = query.Trim().ToUpperInvariant();
        
        // Wait for initialization to complete if it's in progress
        if (_initializationTask != null && !_isConnected.HasValue)
        {
            try
            {
                await _initializationTask.WaitAsync(TimeSpan.FromSeconds(1), ct);
            }
            catch
            {
                // If initialization times out or is cancelled, proceed with dummy data
            }
        }
        
        // If still not initialized, do a quick check (but don't cache if InitializeAsync is running)
        if (!_isConnected.HasValue && _initializationTask == null)
        {
            lock (_connectionCheckLock)
            {
                if (!_isConnected.HasValue)
                {
                    _isConnected = CheckInitialConnection();
                    _logger.LogDebug("Symbol search: Connection check completed lazily, connected={Connected}", _isConnected.Value);
                }
            }
        }
        
        // Only use real IBKR search if connection was successful
        if (_isConnected == true && _ibkrService != null)
        {
            return await SearchViaIbkrAsync(upperQuery, ct);
        }
        
        // Use dummy data if not connected
        var dummyResults = SearchDummyData(upperQuery);
        _logger.LogDebug("Symbol search: Query '{Query}' returned {Count} dummy results", query, dummyResults.Count);
        return dummyResults;
    }
    
    private async Task<IReadOnlyList<SymbolSearchResult>> SearchViaIbkrAsync(string query, CancellationToken ct)
    {
        try
        {
            if (_ibkrService == null)
                return Array.Empty<SymbolSearchResult>();

            var results = await _ibkrService.SearchSymbolsAsync(query, ct);
            if (results.Count > 0)
            {
                _logger.LogDebug("IBKR search returned {Count} results for query: {Query}", results.Count, query);
                return results;
            }
            
            // Return empty if no results (don't fall back to dummy data when connected)
            return Array.Empty<SymbolSearchResult>();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("IBKR symbol search was cancelled");
            return Array.Empty<SymbolSearchResult>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IBKR symbol search failed for query: {Query}", query);
            // Return empty instead of falling back to dummy data when connected
            return Array.Empty<SymbolSearchResult>();
        }
    }
    
    private IReadOnlyList<SymbolSearchResult> SearchDummyData(string query)
    {
        _logger.LogInformation("SearchDummyData: Searching for query '{Query}'", query);
        _logger.LogInformation("SearchDummyData: Dictionary contains {Count} prefix keys", _dummyData.Count);
        
        var results = new HashSet<SymbolSearchResult>(new SymbolEqualityComparer());
        int directMatchCount = 0;
        int exactPrefixMatchCount = 0;
        int partialKeyMatchCount = 0;
        
        // First, search in symbol/company names directly (most accurate)
        foreach (var kvp in _dummyData)
        {
            foreach (var item in kvp.Value)
            {
                if (item.Symbol.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Company.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    if (results.Add(item))
                    {
                        directMatchCount++;
                    }
                }
            }
        }
        
        _logger.LogInformation("SearchDummyData: Direct search phase found {Count} matches (total unique: {Total})", directMatchCount, results.Count);
        
        // Also check indexed prefix matches
        if (_dummyData.TryGetValue(query, out var exactMatches))
        {
            foreach (var match in exactMatches)
            {
                if (results.Add(match))
                {
                    exactPrefixMatchCount++;
                }
            }
            _logger.LogInformation("SearchDummyData: Exact prefix match found {Count} additional matches (total unique: {Total})", exactPrefixMatchCount, results.Count);
        }
        else
        {
            _logger.LogDebug("SearchDummyData: No exact prefix match found for key '{Query}'", query);
        }
        
        // Partial matches from index keys
        foreach (var kvp in _dummyData)
        {
            if (kvp.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var match in kvp.Value)
                {
                    if (results.Add(match))
                    {
                        partialKeyMatchCount++;
                    }
                }
            }
        }
        
        _logger.LogInformation("SearchDummyData: Partial key match phase found {Count} additional matches (total unique: {Total})", partialKeyMatchCount, results.Count);
        _logger.LogInformation("SearchDummyData: Total unique matches before sorting: {Count}", results.Count);
        
        // Sort by relevance: exact symbol prefix matches first, then others
        var sortedResults = results
            .OrderByDescending(r => r.Symbol.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(r => r.Symbol)
            .Take(10)
            .ToList();
        
        _logger.LogInformation("SearchDummyData: After sorting and limiting to 10, returning {Count} results", sortedResults.Count);
        
        if (sortedResults.Count > 0)
        {
            var sampleSymbols = sortedResults.Take(5).Select(r => r.Symbol).ToList();
            _logger.LogInformation("SearchDummyData: Sample matched symbols: {Symbols}", string.Join(", ", sampleSymbols));
        }
        else
        {
            _logger.LogWarning("SearchDummyData: No results found for query '{Query}' - dictionary has {DictCount} keys", query, _dummyData.Count);
        }
        
        return sortedResults;
    }
    
    private class SymbolEqualityComparer : IEqualityComparer<SymbolSearchResult>
    {
        public bool Equals(SymbolSearchResult? x, SymbolSearchResult? y) => 
            x?.Symbol == y?.Symbol;
        public int GetHashCode(SymbolSearchResult obj) => obj.Symbol.GetHashCode();
    }
    
    public void Dispose()
    {
        _disposed = true;
    }
}

