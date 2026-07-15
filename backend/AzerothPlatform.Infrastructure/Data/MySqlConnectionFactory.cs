using System.Data.Common;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace AzerothPlatform.Infrastructure.Data;

/// <summary>
/// Factory for creating MySQL connections to AzerothCore databases
/// </summary>
public class MySqlConnectionFactory : IMySqlConnectionFactory
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly string _mysqlHost;

    public MySqlConnectionFactory(AzerothCoreDbContext dbContext, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _mysqlHost = configuration["MySQL:Host"] ?? "localhost";
    }

    public async Task<DbConnection> CreateConnectionAsync(string stackId, string database, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (stack is null)
        {
            throw new InvalidOperationException($"Stack '{stackId}' not found");
        }

        var dbName = database.ToLowerInvariant() switch
        {
            "auth" => "acore_auth",
            "world" => "acore_world",
            "characters" => "acore_characters",
            _ => throw new ArgumentException($"Unknown database type: {database}. Valid values are: auth, world, characters", nameof(database))
        };

        // Connect to MySQL using configured host (localhost for local dev, host.docker.internal for Docker).
        // The stack's MySQL containers expose their ports to the host. External stacks run on a remote
        // engine, so target their public host (requires the DB port to be reachable on the remote).
        var host = stack.DeploymentTarget == Core.Contracts.DeploymentTarget.External
                   && !string.IsNullOrWhiteSpace(stack.ExternalHost)
            ? stack.ExternalHost
            : _mysqlHost;
        var builder = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)stack.DatabasePort,
            Database = dbName,
            UserID = "root",
            Password = stack.DatabaseRootPassword,
            AllowPublicKeyRetrieval = true
        };

        var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
