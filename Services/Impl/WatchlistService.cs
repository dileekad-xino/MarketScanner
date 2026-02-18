using MarketScanner.Database;
using MarketScanner.Models;
using Microsoft.Extensions.Logging;
using SQLite;

namespace MarketScanner.Services.Impl;

public class WatchlistService : IWatchlistService
{
    private readonly ILogger<WatchlistService> _logger;
    private readonly IDatabaseContext _databaseContext;
    private const string DatabaseFileName = "watchlists.db3";
    public event EventHandler? WatchlistsChanged;

    public WatchlistService(ILogger<WatchlistService> logger, IDatabaseContext databaseContext)
    {
        _logger = logger;
        _databaseContext = databaseContext;
    }

    private async Task<SQLiteAsyncConnection> GetDatabaseAsync()
    {
        return await _databaseContext.GetConnectionAsync(DatabaseFileName);
    }

    public async Task InitializeAsync()
    {
        // Tables are initialized automatically by DatabaseContext
        await _databaseContext.InitializeTablesAsync(DatabaseFileName);
    }

    public async Task<List<Watchlist>> GetAllWatchlistsAsync()
    {
        var database = await GetDatabaseAsync();
        return await database.Table<Watchlist>()
            .OrderBy(w => w.CreatedAt)
            .ToListAsync();
    }

    public async Task<Watchlist> CreateWatchlistAsync(string name)
    {
        var database = await GetDatabaseAsync();

        var watchlist = new Watchlist
        {
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await database.InsertAsync(watchlist);
        _logger.LogInformation("Created watchlist '{Name}' with ID {Id}", name, watchlist.Id);
        WatchlistsChanged?.Invoke(this, EventArgs.Empty);

        return watchlist;
    }

    public async Task<bool> RenameWatchlistAsync(int watchlistId, string newName)
    {
        var database = await GetDatabaseAsync();

        var watchlist = await database.FindAsync<Watchlist>(watchlistId);
        if (watchlist == null)
        {
            _logger.LogWarning("Watchlist {Id} not found for rename", watchlistId);
            return false;
        }

        watchlist.Name = newName;
        watchlist.UpdatedAt = DateTime.UtcNow;

        await database.UpdateAsync(watchlist);
        _logger.LogInformation("Renamed watchlist {Id} to '{NewName}'", watchlistId, newName);
        WatchlistsChanged?.Invoke(this, EventArgs.Empty);

        return true;
    }

    public async Task<bool> DeleteWatchlistAsync(int watchlistId)
    {
        var database = await GetDatabaseAsync();

        // Delete all items first
        await database.ExecuteAsync("DELETE FROM watchlist_items WHERE WatchlistId = ?", watchlistId);

        // Delete watchlist
        var deleted = await database.DeleteAsync<Watchlist>(watchlistId);

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted watchlist {Id} and its items", watchlistId);
            WatchlistsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        _logger.LogWarning("Watchlist {Id} not found for deletion", watchlistId);
        return false;
    }

    public async Task<List<WatchlistItem>> GetWatchlistItemsAsync(int watchlistId)
    {
        var database = await GetDatabaseAsync();

        return await database.Table<WatchlistItem>()
            .Where(wi => wi.WatchlistId == watchlistId)
            .OrderBy(wi => wi.DisplayOrder)
            .ToListAsync();
    }

    public async Task AddItemsAsync(int watchlistId, List<(string Symbol, string Company)> symbolsAndCompanies)
    {
        var database = await GetDatabaseAsync();

        // Get current max display order
        var maxOrder = await database.ExecuteScalarAsync<int>(
            "SELECT COALESCE(MAX(DisplayOrder), -1) FROM watchlist_items WHERE WatchlistId = ?",
            watchlistId);

        var items = new List<WatchlistItem>();
        for (int i = 0; i < symbolsAndCompanies.Count; i++)
        {
            var symbol = symbolsAndCompanies[i].Symbol;
            var company = symbolsAndCompanies[i].Company; // Get company name
            
            // Skip if symbol already exists in this watchlist
            var existing = await database.Table<WatchlistItem>()
                .Where(wi => wi.WatchlistId == watchlistId && wi.Symbol == symbol)
                .CountAsync();

            if (existing > 0)
            {
                _logger.LogDebug("Symbol {Symbol} already exists in watchlist {WatchlistId}, skipping", symbol, watchlistId);
                continue;
            }

            items.Add(new WatchlistItem
            {
                WatchlistId = watchlistId,
                Symbol = symbol,
                Company = company, // Store company name
                DisplayOrder = maxOrder + i + 1,
                AddedAt = DateTime.UtcNow
            });
        }

        if (items.Count > 0)
        {
            await database.InsertAllAsync(items);
            _logger.LogInformation("Added {Count} symbols to watchlist {WatchlistId}", items.Count, watchlistId);
        }

        // Update watchlist timestamp
        await database.ExecuteAsync(
            "UPDATE watchlists SET UpdatedAt = ? WHERE Id = ?",
            DateTime.UtcNow, watchlistId);
    }

    public async Task RemoveItemAsync(int watchlistId, string symbol)
    {
        var database = await GetDatabaseAsync();

        var deleted = await database.ExecuteAsync(
            "DELETE FROM watchlist_items WHERE WatchlistId = ? AND Symbol = ?",
            watchlistId, symbol);

        if (deleted > 0)
        {
            _logger.LogInformation("Removed {Symbol} from watchlist {WatchlistId}", symbol, watchlistId);

            // Update watchlist timestamp
            await database.ExecuteAsync(
                "UPDATE watchlists SET UpdatedAt = ? WHERE Id = ?",
                DateTime.UtcNow, watchlistId);
        }
        else
        {
            _logger.LogWarning("Symbol {Symbol} not found in watchlist {WatchlistId}", symbol, watchlistId);
        }
    }

    public async Task ReorderItemsAsync(int watchlistId, List<string> orderedSymbols)
    {
        var database = await GetDatabaseAsync();

        for (int i = 0; i < orderedSymbols.Count; i++)
        {
            await database.ExecuteAsync(
                "UPDATE watchlist_items SET DisplayOrder = ? WHERE WatchlistId = ? AND Symbol = ?",
                i, watchlistId, orderedSymbols[i]);
        }

        _logger.LogInformation("Reordered {Count} items in watchlist {WatchlistId}", orderedSymbols.Count, watchlistId);

        // Update watchlist timestamp
        await database.ExecuteAsync(
            "UPDATE watchlists SET UpdatedAt = ? WHERE Id = ?",
            DateTime.UtcNow, watchlistId);
    }
}

