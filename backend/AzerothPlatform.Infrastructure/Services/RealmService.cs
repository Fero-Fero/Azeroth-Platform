using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Reads and updates the realms in a stack's <c>acore_auth.realmlist</c> table.
/// </summary>
public class RealmService : IRealmService
{
    private readonly IMySqlConnectionFactory _connectionFactory;
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IStackRegistryService _registry;
    private readonly ILogger<RealmService> _logger;

    public RealmService(
        IMySqlConnectionFactory connectionFactory,
        AzerothCoreDbContext dbContext,
        IStackRegistryService registry,
        ILogger<RealmService> logger)
    {
        _connectionFactory = connectionFactory;
        _dbContext = dbContext;
        _registry = registry;
        _logger = logger;
    }

    public async Task<List<RealmDto>> GetRealmsAsync(string stackId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "auth", cancellationToken);

        const string sql = @"
            SELECT
                id AS Id,
                name AS Name,
                address AS Address,
                port AS Port,
                icon AS Type,
                flag AS Flags,
                timezone AS Timezone,
                allowedSecurityLevel AS AllowedSecurityLevel,
                population AS Population
            FROM realmlist
            ORDER BY id";

        var realms = await connection.QueryAsync<RealmDto>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return realms.ToList();
    }

    public async Task<RealmDto> CreateRealmAsync(string stackId, CreateRealmRequest request, CancellationToken cancellationToken = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 64)
        {
            throw new ArgumentException("Realm name must be between 1 and 64 characters.", nameof(request));
        }

        using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "auth", cancellationToken);

        // Copy network details from an existing realm so the new row is coherent (a stack has one
        // world address). Fall back to standard AzerothCore defaults if the realmlist is empty.
        const string templateSql = @"
            SELECT address AS Address, localAddress AS LocalAddress, localSubnetMask AS LocalSubnetMask,
                   port AS Port, gamebuild AS GameBuild
            FROM realmlist
            ORDER BY id
            LIMIT 1";
        var template = await connection.QuerySingleOrDefaultAsync<RealmTemplate>(
            new CommandDefinition(templateSql, cancellationToken: cancellationToken));

        var address = template?.Address ?? "127.0.0.1";
        var localAddress = template?.LocalAddress ?? "127.0.0.1";
        var localSubnetMask = template?.LocalSubnetMask ?? "255.255.255.0";
        var port = template?.Port ?? 8085;
        var gameBuild = template?.GameBuild ?? 12340;

        const string insertSql = @"
            INSERT INTO realmlist
                (name, address, localAddress, localSubnetMask, port, icon, flag, timezone, allowedSecurityLevel, population, gamebuild)
            VALUES
                (@Name, @Address, @LocalAddress, @LocalSubnetMask, @Port, @Type, @Flags, @Timezone, @AllowedSecurityLevel, 0, @GameBuild);
            SELECT LAST_INSERT_ID();";

        var newId = await connection.ExecuteScalarAsync<int>(new CommandDefinition(insertSql, new
        {
            Name = name,
            Address = address,
            LocalAddress = localAddress,
            LocalSubnetMask = localSubnetMask,
            Port = port,
            request.Type,
            request.Flags,
            request.Timezone,
            request.AllowedSecurityLevel,
            GameBuild = gameBuild
        }, cancellationToken: cancellationToken));

        _logger.LogInformation("Created realm '{RealmName}' (id {RealmId}) for stack {StackId}.", name, newId, stackId);

        var realms = await GetRealmsAsync(stackId, cancellationToken);
        return realms.FirstOrDefault(r => r.Id == newId)
            ?? throw new InvalidOperationException("Realm was created but could not be read back.");
    }

    public async Task<RealmDto> UpdateRealmAsync(string stackId, int realmId, UpdateRealmRequest request, CancellationToken cancellationToken = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 64)
        {
            throw new ArgumentException("Realm name must be between 1 and 64 characters.", nameof(request));
        }

        using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "auth", cancellationToken);

        const string updateSql = @"
            UPDATE realmlist
            SET name = @Name,
                icon = @Type,
                flag = @Flags,
                timezone = @Timezone,
                allowedSecurityLevel = @AllowedSecurityLevel
            WHERE id = @Id";

        var affected = await connection.ExecuteAsync(new CommandDefinition(updateSql, new
        {
            Name = name,
            request.Type,
            request.Flags,
            request.Timezone,
            request.AllowedSecurityLevel,
            Id = realmId
        }, cancellationToken: cancellationToken));

        if (affected == 0)
        {
            throw new KeyNotFoundException($"Realm with id {realmId} was not found for stack '{stackId}'.");
        }

        // The platform rewrites realmlist row 1 (name/address/port) on every stack start, so keep the
        // stored realm name in sync when the primary realm is renamed here — otherwise a restart would
        // revert the name. Row 1 is also the name the armory is configured with.
        if (realmId == 1)
        {
            var stack = await _dbContext.ManagedStacks
                .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
            if (stack is not null && !string.Equals(stack.RealmName, name, StringComparison.Ordinal))
            {
                stack.RealmName = name;
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Synced stored realm name for stack {StackId} to '{RealmName}'.", stackId, name);
            }
        }

        var realms = await GetRealmsAsync(stackId, cancellationToken);
        return realms.FirstOrDefault(r => r.Id == realmId)
            ?? throw new KeyNotFoundException($"Realm with id {realmId} was not found for stack '{stackId}'.");
    }

    public async Task<List<RealmDto>> SetRealmAddressAsync(string stackId, string host, CancellationToken cancellationToken = default)
    {
        host = (host ?? string.Empty).Trim();
        if (host.Length is < 1 or > 255)
        {
            throw new ArgumentException("Realm address must be between 1 and 255 characters.", nameof(host));
        }

        // Persist as the stack's realmlist host override so the address survives stack restarts — the
        // platform rewrites the realmlist row on every start from this value (blank falls back to the
        // global default), so without persisting it here the change would be reverted on the next start.
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            throw new KeyNotFoundException($"Stack '{stackId}' was not found.");
        }

        stack.RealmlistHostOverride = host;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var realmAddress = RealmlistHostResolver.ResolveForRealmAddress(host, cancellationToken);

        // Apply to the live realmlist so the change takes effect immediately without a restart. A stack
        // has a single world address, so every realm row shares it (both the public address clients are
        // redirected to and the localAddress served to same-LAN clients).
        using var connection = await _connectionFactory.CreateConnectionAsync(stackId, "auth", cancellationToken);
        const string updateSql = "UPDATE realmlist SET address = @Host, localAddress = @Host";
        await connection.ExecuteAsync(new CommandDefinition(updateSql, new { Host = realmAddress }, cancellationToken: cancellationToken));

        _logger.LogInformation("Set realmlist address for stack {StackId} to '{Host}'.", stackId, realmAddress);

        // The realmlist host feeds each registry entry's connection info; re-push so launchers pick up
        // the new address across the replicated registry. Best-effort: never fail the address change.
        try
        {
            await _registry.RebuildAndPushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-push registry after realmlist change for stack {StackId}.", stackId);
        }

        return await GetRealmsAsync(stackId, cancellationToken);
    }

    /// <summary>
    /// Network details borrowed from an existing realm row when creating a new one.
    /// </summary>
    private sealed class RealmTemplate
    {
        public string Address { get; set; } = string.Empty;
        public string LocalAddress { get; set; } = string.Empty;
        public string LocalSubnetMask { get; set; } = string.Empty;
        public int Port { get; set; }
        public int GameBuild { get; set; }
    }
}
