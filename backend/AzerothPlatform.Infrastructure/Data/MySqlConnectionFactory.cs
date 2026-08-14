using System.Data.Common;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace AzerothPlatform.Infrastructure.Data;

/// <summary>
/// Factory for creating MySQL connections to AzerothCore databases
/// </summary>
public class MySqlConnectionFactory : IMySqlConnectionFactory
{
    private const int MysqlContainerPort = 3306;

    private readonly AzerothCoreDbContext _dbContext;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly ILogger<MySqlConnectionFactory> _logger;
    private readonly string _mysqlHost;

    public MySqlConnectionFactory(
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine,
        IConfiguration configuration,
        ILogger<MySqlConnectionFactory> logger)
    {
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
        _logger = logger;
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
            var remotePort = stack.DatabasePort;
            var containerName = DockerComposeOverrideGenerator.GetContainerNameForService(
                stack.Id,
                stack.StackName,
                "ac-database");
            if (containerName is not null)
            {
                var publishedPort = await _remoteEngine.TryResolveRemotePublishedPortAsync(
                    stack,
                    containerName,
                    MysqlContainerPort,
                    cancellationToken);
                if (publishedPort is > 0 && publishedPort != remotePort)
                {
                    _logger.LogWarning(
                        "Stack {StackId} database is published on host port {PublishedPort} but configured as {ConfiguredPort}; using the live published port.",
                        stack.Id,
                        publishedPort,
                        remotePort);
                    remotePort = publishedPort.Value;
                }
            }

            // External stacks: reach MySQL over an SSH tunnel to loopback on the remote host. Data-plane
            // ports must publish on 127.0.0.1 there — tunneling to a VPC/public bind IP breaks the stream.
            var endpoint = await _remoteEngine.GetManagementTunnelEndpointAsync(
                stack,
                remotePort,
                "127.0.0.1",
                cancellationToken);
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
            SslMode = MySqlSslMode.Disabled,
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
