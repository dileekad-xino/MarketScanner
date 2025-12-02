using Microsoft.Extensions.Logging;
using SQLite;
using System.Collections.Concurrent;

namespace MarketScanner.Database;

/// <summary>
/// Manages table registration and initialization for databases.
/// </summary>
public class DatabaseInitializer
{
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly ConcurrentDictionary<string, HashSet<Type>> _registeredTables = new();

    public DatabaseInitializer(ILogger<DatabaseInitializer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a table type for a specific database.
    /// </summary>
    /// <typeparam name="T">The table model type</typeparam>
    /// <param name="databaseFileName">The database file name</param>
    public void RegisterTable<T>(string databaseFileName) where T : new()
    {
        var tables = _registeredTables.GetOrAdd(databaseFileName, _ => new HashSet<Type>());
        tables.Add(typeof(T));
        _logger.LogDebug("Registered table {TableType} for database {Database}", typeof(T).Name, databaseFileName);
    }

    /// <summary>
    /// Initializes all registered tables for a database.
    /// </summary>
    /// <param name="connection">The database connection</param>
    /// <param name="databaseFileName">The database file name</param>
    /// <returns>Task representing the async operation</returns>
    public async Task InitializeAllTablesAsync(SQLiteAsyncConnection connection, string databaseFileName)
    {
        if (!_registeredTables.TryGetValue(databaseFileName, out var tables) || tables.Count == 0)
        {
            _logger.LogWarning("No tables registered for database {Database}", databaseFileName);
            return;
        }

        _logger.LogInformation("Initializing {Count} tables for database {Database}", tables.Count, databaseFileName);

        foreach (var tableType in tables)
        {
            try
            {
                // Use reflection to call CreateTableAsync<T> for each registered type
                var method = typeof(SQLiteAsyncConnection).GetMethod(nameof(SQLiteAsyncConnection.CreateTableAsync), new[] { typeof(CreateFlags) });
                if (method != null)
                {
                    var genericMethod = method.MakeGenericMethod(tableType);
                    var task = (Task)genericMethod.Invoke(connection, new object[] { CreateFlags.None })!;
                    await task;
                    _logger.LogDebug("Created table {TableType} in database {Database}", tableType.Name, databaseFileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create table {TableType} in database {Database}", tableType.Name, databaseFileName);
                throw;
            }
        }

        _logger.LogInformation("Successfully initialized all tables for database {Database}", databaseFileName);
    }

    /// <summary>
    /// Gets all registered table types for a database.
    /// </summary>
    /// <param name="databaseFileName">The database file name</param>
    /// <returns>Collection of registered table types</returns>
    public IReadOnlyCollection<Type> GetRegisteredTables(string databaseFileName)
    {
        if (_registeredTables.TryGetValue(databaseFileName, out var tables))
        {
            return tables;
        }
        return Array.Empty<Type>();
    }
}

