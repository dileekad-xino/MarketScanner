using MarketScanner.Database;
using MarketScanner.Models;
using Microsoft.Extensions.Logging;
using SQLite;

namespace MarketScanner.Services.Impl;

public class TradeService : ITradeService
{
    private readonly ILogger<TradeService> _logger;
    private readonly IDatabaseContext _databaseContext;
    private const string DatabaseFileName = "trades.db3";

    public TradeService(ILogger<TradeService> logger, IDatabaseContext databaseContext)
    {
        _logger = logger;
        _databaseContext = databaseContext;
    }

    private async Task<SQLiteAsyncConnection> GetDatabaseAsync()
    {
        var database = await _databaseContext.GetConnectionAsync(DatabaseFileName);
        var needsRecreation = await MigrateTradeTableAsync(database);
        
        // If migration recreated the database, get a fresh connection
        if (needsRecreation)
        {
            database = await _databaseContext.GetConnectionAsync(DatabaseFileName);
        }
        
        return database;
    }

    /// <summary>
    /// Checks if the Trade table has the old schema and recreates the database if needed.
    /// </summary>
    /// <returns>True if database was recreated, false otherwise</returns>
    private async Task<bool> MigrateTradeTableAsync(SQLiteAsyncConnection database)
    {
        try
        {
            _logger.LogInformation("Checking Trade table schema...");
            
            // Check if table exists
            var tableExists = await database.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='trades'") > 0;
            
            if (!tableExists)
            {
                // Table doesn't exist yet, will be created by DatabaseInitializer
                _logger.LogInformation("Trade table does not exist yet, will be created with new schema");
                return false;
            }

            // Check schema by looking at ExitPrice column constraints
            var columnInfo = await database.QueryAsync<ColumnInfo>("PRAGMA table_info(trades)");
            var exitPriceColumn = columnInfo.FirstOrDefault(col => col.name.Equals("ExitPrice", StringComparison.OrdinalIgnoreCase));
            
            if (exitPriceColumn == null)
            {
                _logger.LogWarning("ExitPrice column not found in trades table - recreating database");
                await RecreateDatabaseAsync();
                return true;
            }

            // Check if ExitPrice has NOT NULL constraint (old schema) or if Status column is missing
            bool hasStatusColumn = columnInfo.Any(col => col.name.Equals("Status", StringComparison.OrdinalIgnoreCase));
            bool exitPriceIsNotNull = exitPriceColumn.notnull == 1;
            
            if (exitPriceIsNotNull || !hasStatusColumn)
            {
                _logger.LogInformation("Old schema detected (ExitPrice NOT NULL={ExitPriceNotNull}, HasStatus={HasStatus}) - recreating database", 
                    exitPriceIsNotNull, hasStatusColumn);
                await RecreateDatabaseAsync();
                return true;
            }
            else
            {
                _logger.LogInformation("Trade table already has new schema - no migration needed");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Trade table schema check");
            throw;
        }
    }

    /// <summary>
    /// Recreates the trades database by deleting the file and closing the connection.
    /// </summary>
    private async Task RecreateDatabaseAsync()
    {
        try
        {
            var dbPath = Path.Combine(FileSystem.AppDataDirectory, DatabaseFileName);
            
            // Close the existing connection
            await _databaseContext.CloseConnectionAsync(DatabaseFileName);
            
            // Wait a bit and force GC to ensure file handles are released
            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100);
            
            // Delete the database file if it exists, with retry logic
            if (File.Exists(dbPath))
            {
                _logger.LogInformation("Deleting old database file: {DbPath}", dbPath);
                
                const int maxRetries = 5;
                const int delayMs = 200;
                
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        File.Delete(dbPath);
                        _logger.LogInformation("Database file deleted successfully on attempt {Attempt}", attempt);
                        break;
                    }
                    catch (IOException ex) when (attempt < maxRetries)
                    {
                        _logger.LogWarning("Failed to delete database file on attempt {Attempt}, retrying in {Delay}ms: {Error}", 
                            attempt, delayMs, ex.Message);
                        await Task.Delay(delayMs);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
            }
            
            _logger.LogInformation("Database file deleted - will be recreated with new schema on next access");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recreating database");
            throw;
        }
    }

    // Helper class for reading column info
    private class ColumnInfo
    {
        public int cid { get; set; }
        public string name { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public int notnull { get; set; }
        public object? dflt_value { get; set; }
        public int pk { get; set; }
    }

    public async Task SaveTradeAsync(Trade trade)
    {
        var database = await GetDatabaseAsync();
        await database.InsertAsync(trade);
        _logger.LogInformation("Saved trade: {Symbol} Entry={EntryPrice:C2} Status={Status} P/L={PL:C2}",
            trade.Symbol, trade.EntryPrice, trade.Status, trade.ProfitLoss);
    }

    public async Task UpdateTradeAsync(Trade trade)
    {
        var database = await GetDatabaseAsync();
        await database.UpdateAsync(trade);
        _logger.LogInformation("Updated trade: {Symbol} Entry={EntryPrice:C2} Exit={ExitPrice:C2} Status={Status} P/L={PL:C2}",
            trade.Symbol, trade.EntryPrice, trade.ExitPrice, trade.Status, trade.ProfitLoss);
    }

    public async Task<Trade?> GetTradeByIdAsync(int id)
    {
        var database = await GetDatabaseAsync();
        return await database.Table<Trade>().Where(t => t.Id == id).FirstOrDefaultAsync();
    }

    public async Task<List<Trade>> GetTradesByDateAsync(DateTime date)
    {
        var database = await GetDatabaseAsync();
        var startOfDay = date.Date;
        var endOfDay = startOfDay.AddDays(1);

        // Fetch all trades (open or closed) that were active on the selected date
        // A trade is active if its entry time is on or before the end of the day,
        // AND its exit time is null (open) or on or after the start of the day.
        // First get all trades, then filter in memory to avoid nullable DateTime LINQ issues
        var allTrades = await database.Table<Trade>().ToListAsync();
        
        return allTrades
            .Where(t => t.EntryTime < endOfDay && (t.ExitTime == null || t.ExitTime.Value >= startOfDay))
            .OrderByDescending(t => t.EntryTime)
            .ToList();
    }

    public async Task<List<Trade>> GetOpenTradesAsync()
    {
        var database = await GetDatabaseAsync();
        return await database.Table<Trade>()
            .Where(t => t.Status == TradeStatus.Open)
            .OrderByDescending(t => t.EntryTime)
            .ToListAsync();
    }

    public async Task<List<Trade>> GetTradesBySymbolAsync(string symbol)
    {
        var database = await GetDatabaseAsync();
        return await database.Table<Trade>()
            .Where(t => t.Symbol == symbol)
            .OrderByDescending(t => t.EntryTime)
            .ToListAsync();
    }

    public async Task<List<Trade>> GetAllTradesAsync()
    {
        var database = await GetDatabaseAsync();
        return await database.Table<Trade>()
            .OrderByDescending(t => t.EntryTime)
            .ToListAsync();
    }

    public async Task<(decimal TotalPL, decimal TotalPLPercent)> GetDailyPLAsync(DateTime date)
    {
        var database = await GetDatabaseAsync();
        var startOfDay = date.Date;
        var endOfDay = startOfDay.AddDays(1);

        // Only count closed trades for daily P/L
        // Get all closed trades first, then filter in memory to avoid nullable DateTime LINQ issues
        var allClosedTrades = await database.Table<Trade>()
            .Where(t => t.Status == TradeStatus.Closed)
            .ToListAsync();
        
        var closedTrades = allClosedTrades
            .Where(t => t.ExitTime.HasValue && 
                       t.ExitTime.Value >= startOfDay && 
                       t.ExitTime.Value < endOfDay)
            .ToList();
        
        if (closedTrades.Count == 0)
        {
            return (0, 0);
        }

        var totalPL = closedTrades.Sum(t => t.ProfitLoss);
        
        // Calculate weighted average P/L percentage
        var totalEntryValue = closedTrades.Sum(t => t.EntryPrice * t.Quantity);
        var totalPLPercent = totalEntryValue > 0 
            ? (totalPL / totalEntryValue) * 100 
            : 0;

        return (totalPL, totalPLPercent);
    }

    public async Task<List<Trade>> GetTradesByDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        var database = await GetDatabaseAsync();
        var start = startDate.Date;
        var end = endDate.Date.AddDays(1);

        return await database.Table<Trade>()
            .Where(t => t.EntryTime >= start && t.EntryTime < end)
            .OrderByDescending(t => t.EntryTime)
            .ToListAsync();
    }
}

