using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

/// <summary>
/// IBKR-backed symbol search service.
/// Returns no results when IBKR is unavailable.
/// </summary>
public class SymbolSearchService : ISymbolSearchService, IDisposable
{
    private readonly IbkrGatewayService? _ibkrService;
    private readonly ILogger<SymbolSearchService> _logger;

    public bool IsConnected => _ibkrService != null && _ibkrService.IsConnected;

    public SymbolSearchService(
        IbkrGatewayService? ibkrService,
        ILogger<SymbolSearchService> logger)
    {
        _ibkrService = ibkrService;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_ibkrService == null)
        {
            _logger.LogInformation("Symbol search service: IBKR gateway unavailable");
            return;
        }

        using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var connected = await _ibkrService.TryReconnectAsync(startupCts.Token);
        _logger.LogInformation(
            connected
                ? "Symbol search service: IBKR gateway connection detected"
                : "Symbol search service: IBKR connection unavailable");
    }

    public async Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Array.Empty<SymbolSearchResult>();

        if (_ibkrService == null)
            return Array.Empty<SymbolSearchResult>();

        if (!_ibkrService.IsConnected)
        {
            using var reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reconnectCts.CancelAfter(TimeSpan.FromSeconds(2));
            await _ibkrService.TryReconnectAsync(reconnectCts.Token);
        }

        if (!_ibkrService.IsConnected)
            return Array.Empty<SymbolSearchResult>();

        try
        {
            var upperQuery = query.Trim().ToUpperInvariant();
            var results = await _ibkrService.SearchSymbolsAsync(upperQuery, ct);
            if (results.Count > 0)
            {
                _logger.LogDebug("IBKR search returned {Count} results for query: {Query}", results.Count, query);
            }
            return results;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("IBKR symbol search was cancelled");
            return Array.Empty<SymbolSearchResult>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IBKR symbol search failed for query: {Query}", query);
            return Array.Empty<SymbolSearchResult>();
        }
    }

    public void Dispose()
    {
        // no-op
    }
}
