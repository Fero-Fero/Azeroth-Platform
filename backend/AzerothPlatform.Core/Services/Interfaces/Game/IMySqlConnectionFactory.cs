using System.Data.Common;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Factory for creating database connections to AzerothCore databases
/// </summary>
public interface IMySqlConnectionFactory
{
    /// <summary>
    /// Create a database connection to the specified database for the given stack
    /// </summary>
    /// <param name="stackId">Stack identifier</param>
    /// <param name="database">Database name (auth, world, or characters)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An open DbConnection</returns>
    Task<DbConnection> CreateConnectionAsync(string stackId, string database, CancellationToken cancellationToken = default);
}
