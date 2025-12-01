using MarketScanner.Models;

namespace MarketScanner.Services;

/// <summary>
/// Service for managing trade records (closed positions).
/// </summary>
public interface ITradeService
{
    /// <summary>
    /// Saves a trade to the database.
    /// </summary>
    Task SaveTradeAsync(Trade trade);

    /// <summary>
    /// Gets all trades for a specific date.
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
    /// Gets aggregate daily P/L for a specific date.
    /// </summary>
    Task<(decimal TotalPL, decimal TotalPLPercent)> GetDailyPLAsync(DateTime date);

    /// <summary>
    /// Gets trades within a date range.
    /// </summary>
    Task<List<Trade>> GetTradesByDateRangeAsync(DateTime startDate, DateTime endDate);
}

