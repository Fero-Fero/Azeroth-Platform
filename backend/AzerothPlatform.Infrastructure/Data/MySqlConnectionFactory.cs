using System.Data.Common;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services;
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
    private readonly IRemoteEngineService _remoteEngine;
    private readonly string _mysqlHost;

    public MySqlConnectionFactory(
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
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

        string host;
        uint port;
        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            // External stacks: reach MySQL over an SSH tunnel so the DB port stays closed on the cloud SG.
            var endpoint = await _remoteEngine.GetManagementTunnelEndpointAsync(stack, stack.DatabasePort, cancellationToken);
            host = endpoint.Host;
            port = (uint)endpoint.Port;
        }
        else
        {
            host = _mysqlHost;
            port = (uint)stack.DatabasePort;
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = port,
            Database = dbName,
            UserID = "root",
            Password = stack.DatabaseRootPassword,
            AllowPublicKeyRetrieval = true,
            ConnectionTimeout = 15,
        };

        var connection = new MySqlConnection(builder.ConnectionString);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(20));
            await connection.OpenAsync(connectCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out connecting to MySQL for stack '{stackId}' at {host}:{port}. " +
                "For external stacks, verify SSH access and that the database container is running on the remote host.");
        }
        catch (MySqlException ex)
        {
            throw new InvalidOperationException(
                stack.DeploymentTarget == DeploymentTarget.External
                    ? $"Could not connect to MySQL on the external stack via SSH tunnel ({host}:{port}): {ex.Message}"
                    : $"Could not connect to MySQL at {host}:{port}: {ex.Message}",
                ex);
        }

        return connection;
    }
}
