using Microsoft.Extensions.Logging;
using SQLite;
using System.Collections.Concurrent;

namespace MarketScanner.Database;

/// <summary>
/// Manages SQLite database connections with lazy initialization and connection pooling.
/// </summary>
public class DatabaseContext : IDatabaseContext
{
    private readonly ILogger<DatabaseContext> _logger;
    private readonly DatabaseInitializer _initializer;
    private readonly ConcurrentDictionary<string, SQLiteAsyncConnection> _connections = new();
    private readonly ConcurrentDictionary<string, bool> _initialized = new();

    public DatabaseContext(ILogger<DatabaseContext> logger, DatabaseInitializer initializer)
    {
        _logger = logger;
        _initializer = initializer;
    }

    /// <summary>
    /// Gets or creates a SQLite connection for the specified database file.
    /// </summary>
    public async Task<SQLiteAsyncConnection> GetConnectionAsync(string databaseFileName)
    {
        // Return existing connection if available
        if (_connections.TryGetValue(databaseFileName, out var existingConnection))
        {
            return existingConnection;
        }

        // Create new connection
        var dbPath = Path.Combine(FileSystem.AppDataDirectory, databaseFileName);
        _logger.LogInformation("Creating database connection for {Database} at {Path}", databaseFileName, dbPath);

        var connection = new SQLiteAsyncConnection(dbPath);
        _connections[databaseFileName] = connection;

        // Initialize tables if not already done
        if (!_initialized.ContainsKey(databaseFileName))
        {
            await _initializer.InitializeAllTablesAsync(connection, databaseFileName);
            _initialized[databaseFileName] = true;
        }

        return connection;
    }

    /// <summary>
    /// Initializes all registered tables for a database.
    /// </summary>
    public async Task InitializeTablesAsync(string databaseFileName)
    {
        var connection = await GetConnectionAsync(databaseFileName);
        // Tables are already initialized in GetConnectionAsync, but this allows explicit re-initialization
        if (!_initialized.ContainsKey(databaseFileName))
        {
            await _initializer.InitializeAllTablesAsync(connection, databaseFileName);
            _initialized[databaseFileName] = true;
        }
    }

    /// <summary>
    /// Closes a database connection.
    /// </summary>
    public async Task CloseConnectionAsync(string databaseFileName)
    {
        if (_connections.TryRemove(databaseFileName, out var connection))
        {
            _logger.LogInformation("Closing database connection for {Database}", databaseFileName);
            await connection.CloseAsync();
            _initialized.TryRemove(databaseFileName, out _);
        }
    }
}

