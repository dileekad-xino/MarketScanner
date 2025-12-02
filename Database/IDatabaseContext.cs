using SQLite;

namespace MarketScanner.Database;

/// <summary>
/// Interface for managing database connections and initialization.
/// </summary>
public interface IDatabaseContext
{
    /// <summary>
    /// Gets or creates a SQLite connection for the specified database file.
    /// </summary>
    /// <param name="databaseFileName">The database file name (e.g., "watchlists.db3")</param>
    /// <returns>A SQLiteAsyncConnection for the database</returns>
    Task<SQLiteAsyncConnection> GetConnectionAsync(string databaseFileName);

    /// <summary>
    /// Initializes all registered tables for a database.
    /// </summary>
    /// <param name="databaseFileName">The database file name</param>
    /// <returns>Task representing the async operation</returns>
    Task InitializeTablesAsync(string databaseFileName);

    /// <summary>
    /// Closes a database connection.
    /// </summary>
    /// <param name="databaseFileName">The database file name</param>
    /// <returns>Task representing the async operation</returns>
    Task CloseConnectionAsync(string databaseFileName);
}

