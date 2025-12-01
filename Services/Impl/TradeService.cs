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
        return await _databaseContext.GetConnectionAsync(DatabaseFileName);
    }

    public async Task SaveTradeAsync(Trade trade)
    {
        var database = await GetDatabaseAsync();
        await database.InsertAsync(trade);
        _logger.LogInformation("Saved trade: {Symbol} Entry={EntryPrice:C2} Exit={ExitPrice:C2} P/L={PL:C2}",
            trade.Symbol, trade.EntryPrice, trade.ExitPrice, trade.ProfitLoss);
    }

    public async Task<List<Trade>> GetTradesByDateAsync(DateTime date)
    {
        var database = await GetDatabaseAsync();
        var startOfDay = date.Date;
        var endOfDay = startOfDay.AddDays(1);

        return await database.Table<Trade>()
            .Where(t => t.ExitTime >= startOfDay && t.ExitTime < endOfDay)
            .OrderByDescending(t => t.ExitTime)
            .ToListAsync();
    }

    public async Task<List<Trade>> GetTradesBySymbolAsync(string symbol)
    {
        var database = await GetDatabaseAsync();
        return await database.Table<Trade>()
            .Where(t => t.Symbol == symbol)
            .OrderByDescending(t => t.ExitTime)
            .ToListAsync();
    }

    public async Task<List<Trade>> GetAllTradesAsync()
    {
        var database = await GetDatabaseAsync();
        return await database.Table<Trade>()
            .OrderByDescending(t => t.ExitTime)
            .ToListAsync();
    }

    public async Task<(decimal TotalPL, decimal TotalPLPercent)> GetDailyPLAsync(DateTime date)
    {
        var trades = await GetTradesByDateAsync(date);
        
        if (trades.Count == 0)
        {
            return (0, 0);
        }

        var totalPL = trades.Sum(t => t.ProfitLoss);
        
        // Calculate weighted average P/L percentage
        var totalEntryValue = trades.Sum(t => t.EntryPrice * t.Quantity);
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
            .Where(t => t.ExitTime >= start && t.ExitTime < end)
            .OrderByDescending(t => t.ExitTime)
            .ToListAsync();
    }
}

