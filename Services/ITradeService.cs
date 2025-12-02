using MarketScanner.Models;

namespace MarketScanner.Services;

/// <summary>
/// Service for managing trade records (both open and closed positions).
/// </summary>
public interface ITradeService
{
    /// <summary>
    /// Saves a new trade to the database.
    /// </summary>
    Task SaveTradeAsync(Trade trade);

    /// <summary>
    /// Updates an existing trade in the database.
    /// </summary>
    Task UpdateTradeAsync(Trade trade);

    /// <summary>
    /// Gets a trade by its ID.
    /// </summary>
    Task<Trade?> GetTradeByIdAsync(int id);

    /// <summary>
    /// Gets all trades (open and closed) for a specific date based on entry time.
    /// </summary>
    Task<List<Trade>> GetTradesByDateAsync(DateTime date);

    /// <summary>
    /// Gets all trades for a specific symbol.
    /// </summary>
    Task<List<Trade>> GetTradesBySymbolAsync(string symbol);

    /// <summary>
    /// Gets all trades.
    /// </summary>
    Task<List<Trade>> GetAllTradesAsync();

    /// <summary>
    /// Gets all open positions.
    /// </summary>
    Task<List<Trade>> GetOpenTradesAsync();

    /// <summary>
    /// Gets aggregate daily P/L for a specific date (closed trades only).
    /// </summary>
    Task<(decimal TotalPL, decimal TotalPLPercent)> GetDailyPLAsync(DateTime date);

    /// <summary>
    /// Gets trades within a date range.
    /// </summary>
    Task<List<Trade>> GetTradesByDateRangeAsync(DateTime startDate, DateTime endDate);
}

