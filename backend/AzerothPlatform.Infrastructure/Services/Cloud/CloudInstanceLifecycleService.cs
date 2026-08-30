using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Cloud;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services;

public sealed class CloudInstanceLifecycleService : ICloudInstanceLifecycleService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;
    private readonly ICloudProviderConnectionService _connections;
    private readonly DigitalOceanClient _digitalOceanClient;
    private readonly AwsEc2Client _awsEc2Client;
    private readonly HetznerCloudClient _hetznerCloudClient;
    private readonly VultrClient _vultrClient;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly IAwsCredentialResolver _awsCredentialResolver;
    private readonly IDigitalOceanTokenResolver _digitalOceanTokenResolver;
    private readonly IVultrTokenResolver _vultrTokenResolver;

    public CloudInstanceLifecycleService(
        AzerothCoreDbContext dbContext,
        ISecretProtector secretProtector,
        ICloudProviderConnectionService connections,
        DigitalOceanClient digitalOceanClient,
        AwsEc2Client awsEc2Client,
        HetznerCloudClient hetznerCloudClient,
        VultrClient vultrClient,
        ICloudAuditService cloudAuditService,
        IAwsCredentialResolver awsCredentialResolver,
        IDigitalOceanTokenResolver digitalOceanTokenResolver,
        IVultrTokenResolver vultrTokenResolver)
    {
        _dbContext = dbContext;
        _secretProtector = secretProtector;
        _connections = connections;
        _digitalOceanClient = digitalOceanClient;
        _awsEc2Client = awsEc2Client;
        _hetznerCloudClient = hetznerCloudClient;
        _vultrClient = vultrClient;
        _cloudAuditService = cloudAuditService;
        _awsCredentialResolver = awsCredentialResolver;
        _digitalOceanTokenResolver = digitalOceanTokenResolver;
        _vultrTokenResolver = vultrTokenResolver;
    }

    public async Task TerminateStackInstanceAsync(
        ManagedStackCloudTarget target,
        CancellationToken cancellationToken = default)
    {
        var match = await ResolveTargetAsync(target, cancellationToken);
        await TerminateResolvedAsync(match.Connection, match.Instance, cancellationToken);

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.InstanceTerminated,
                ResourceType = "instance",
                ResourceId = match.Instance.Id,
                Summary = $"Terminated {match.Connection.Provider} instance {match.Instance.Id} for stack {target.StackName}.",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    stackId = target.StackId,
                    connectionId = match.Connection.Id,
                    provider = match.Connection.Provider,
                    instanceId = match.Instance.Id,
                    region = match.Instance.Region,
                    publicHost = match.Instance.PublicHost,
                }),
            },
            cancellationToken);
    }

    private async Task<(CloudProviderConnectionEntity Connection, CloudInstanceDto Instance)> ResolveTargetAsync(
        ManagedStackCloudTarget target,
        CancellationToken cancellationToken)
    {
        var connectionId = (target.CloudConnectionId ?? string.Empty).Trim();
        var instanceId = (target.CloudInstanceId ?? string.Empty).Trim();
        var region = (target.CloudRegion ?? string.Empty).Trim();
        var host = (target.PublicHost ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(connectionId) && !string.IsNullOrWhiteSpace(instanceId))
        {
            var connection = await LoadConnectionAsync(connectionId, cancellationToken);
            return (
                connection,
                new CloudInstanceDto
                {
                    Id = instanceId,
                    Provider = ParseProvider(connection.Provider),
                    Name = instanceId,
                    Region = region,
                    PublicHost = host,
                });
        }

        var connections = string.IsNullOrWhiteSpace(connectionId)
            ? await _dbContext.CloudProviderConnections.AsNoTracking().ToListAsync(cancellationToken)
            : [await LoadConnectionAsync(connectionId, cancellationToken)];

        if (connections.Count == 0)
        {
            throw new InvalidOperationException(
                "No cloud account is linked. Connect a cloud account, or remove the stack from the manager without terminating a VM.");
        }

        CloudProviderConnectionEntity? matchedConnection = null;
        CloudInstanceDto? matchedInstance = null;

        foreach (var connection in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = ParseProvider(connection.Provider);
            if (provider == CloudProvider.Aws && !string.IsNullOrWhiteSpace(host))
            {
                try
                {
                    var credentials = await _awsCredentialResolver.ResolveAsync(connection, cancellationToken);
                    var awsTarget = await _awsEc2Client.ResolveInstanceForFirewallAsync(
                        credentials,
                        host,
                        string.IsNullOrWhiteSpace(region) ? connection.DefaultRegion : region,
                        instanceId,
                        cancellationToken);
                    matchedConnection = connection;
                    matchedInstance = new CloudInstanceDto
                    {
                        Id = awsTarget.InstanceId,
                        Provider = CloudProvider.Aws,
                        Name = awsTarget.InstanceId,
                        Region = awsTarget.Region,
                        PublicHost = awsTarget.PublicHost,
                    };
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Try the next linked account.
                }
            }

            IReadOnlyList<CloudInstanceDto> instances;
            try
            {
                instances = await _connections.ListInstancesAsync(connection.Id, region, cancellationToken);
            }
            catch
            {
                continue;
            }

            var hostMatches = instances
                .Where(item => HostMatches(item.PublicHost, host) || HostMatches(item.Id, instanceId))
                .ToList();
            if (hostMatches.Count == 1)
            {
                matchedConnection = connection;
                matchedInstance = hostMatches[0];
                break;
            }

            if (hostMatches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"More than one cloud VM matches host {host}. Terminate the extra instances in the provider console, then try again.");
            }
        }

        if (matchedConnection is null || matchedInstance is null)
        {
            throw new InvalidOperationException(
                $"No cloud VM matching {host} was found on linked accounts. The instance may already be gone, or GCP/Azure terminate is not automated yet.");
        }

        return (matchedConnection, matchedInstance);
    }

    private async Task TerminateResolvedAsync(
        CloudProviderConnectionEntity connection,
        CloudInstanceDto instance,
        CancellationToken cancellationToken)
    {
        var provider = ParseProvider(connection.Provider);
        switch (provider)
        {
            case CloudProvider.Aws:
                var credentials = await _awsCredentialResolver.ResolveAsync(connection, cancellationToken);
                var region = string.IsNullOrWhiteSpace(instance.Region)
                    ? connection.DefaultRegion
                    : instance.Region;
                await _awsEc2Client.TerminateInstanceAsync(credentials, region ?? string.Empty, instance.Id, cancellationToken);
                return;
            case CloudProvider.DigitalOcean:
                if (!long.TryParse(instance.Id, out var dropletId))
                {
                    throw new InvalidOperationException($"Invalid DigitalOcean droplet id '{instance.Id}'.");
                }

                await _digitalOceanClient.DeleteDropletAsync(
                    await _digitalOceanTokenResolver.ResolveAsync(connection, cancellationToken),
                    dropletId,
                    cancellationToken);
                return;
            case CloudProvider.Hetzner:
                if (!long.TryParse(instance.Id, out var serverId))
                {
                    throw new InvalidOperationException($"Invalid Hetzner server id '{instance.Id}'.");
                }

                await _hetznerCloudClient.DeleteServerAsync(
                    CloudProviderCredentialStore.UnprotectApiToken(_secretProtector, connection.ProtectedCredentials),
                    serverId,
                    cancellationToken);
                return;
            case CloudProvider.Vultr:
                await _vultrClient.DeleteInstanceAsync(
                    await _vultrTokenResolver.ResolveAsync(connection, cancellationToken),
                    instance.Id,
                    cancellationToken);
                return;
            default:
                throw new InvalidOperationException(
                    $"{provider} cannot terminate VMs from the platform yet. Destroy the instance in the provider console, then remove the stack from the manager.");
        }
    }

    private async Task<CloudProviderConnectionEntity> LoadConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken)
        => await _dbContext.CloudProviderConnections.AsNoTracking()
               .FirstOrDefaultAsync(connection => connection.Id == connectionId, cancellationToken)
           ?? throw new KeyNotFoundException("Cloud connection not found.");

    private static CloudProvider ParseProvider(string provider)
    {
        if (!Enum.TryParse<CloudProvider>(provider, ignoreCase: true, out var parsed))
        {
            throw new InvalidOperationException("Unknown cloud provider on this connection.");
        }

        return parsed;
    }

    private static bool HostMatches(string actual, string expected)
    {
        var left = NormalizeHost(actual);
        var right = NormalizeHost(expected);
        return !string.IsNullOrWhiteSpace(left)
               && !string.IsNullOrWhiteSpace(right)
               && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string value)
        => (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
}
