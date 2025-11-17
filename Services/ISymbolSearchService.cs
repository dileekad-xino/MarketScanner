namespace MarketScanner.Services;

/// <summary>
/// Service for searching symbols with autocomplete functionality.
/// Supports both dummy data (offline) and real IBKR gateway search (when connected).
/// </summary>
public interface ISymbolSearchService
{
    /// <summary>
    /// Searches for symbols matching the query string.
    /// Requires at least 2 characters to return results.
    /// </summary>
    /// <param name="query">Search query (minimum 2 characters)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of matching symbol search results</returns>
    Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(string query, CancellationToken ct = default);
    
    /// <summary>
    /// Indicates whether the service is connected to IBKR gateway.
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Initializes the service by checking connection status once at application startup.
    /// Should be called after the initial IBKR connection attempt.
    /// </summary>
    Task InitializeAsync();
}

/// <summary>
/// Represents a symbol search result.
/// </summary>
public class SymbolSearchResult
{
    public string Symbol { get; set; } = "";
    public string Company { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string SecType { get; set; } = "STK";
}

