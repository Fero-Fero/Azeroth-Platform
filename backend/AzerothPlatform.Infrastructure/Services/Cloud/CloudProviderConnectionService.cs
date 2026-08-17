using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Cloud;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services;

public sealed class CloudProviderConnectionService : ICloudProviderConnectionService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;
    private readonly DigitalOceanClient _digitalOceanClient;
    private readonly AwsEc2Client _awsEc2Client;
    private readonly GcpComputeClient _gcpComputeClient;
    private readonly AzureComputeClient _azureComputeClient;
    private readonly HetznerCloudClient _hetznerCloudClient;
    private readonly VultrClient _vultrClient;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly IAwsCredentialResolver _awsCredentialResolver;
    private readonly IDigitalOceanTokenResolver _digitalOceanTokenResolver;
    private readonly IVultrTokenResolver _vultrTokenResolver;
    private readonly IGcpCredentialResolver _gcpCredentialResolver;
    private readonly IAzureCredentialResolver _azureCredentialResolver;

    public CloudProviderConnectionService(
        AzerothCoreDbContext dbContext,
        ISecretProtector secretProtector,
        DigitalOceanClient digitalOceanClient,
        AwsEc2Client awsEc2Client,
        GcpComputeClient gcpComputeClient,
        AzureComputeClient azureComputeClient,
        HetznerCloudClient hetznerCloudClient,
        VultrClient vultrClient,
        ICloudAuditService cloudAuditService,
        IAwsCredentialResolver awsCredentialResolver,
        IDigitalOceanTokenResolver digitalOceanTokenResolver,
        IVultrTokenResolver vultrTokenResolver,
        IGcpCredentialResolver gcpCredentialResolver,
        IAzureCredentialResolver azureCredentialResolver)
    {
        _dbContext = dbContext;
        _secretProtector = secretProtector;
        _digitalOceanClient = digitalOceanClient;
        _awsEc2Client = awsEc2Client;
        _gcpComputeClient = gcpComputeClient;
        _azureComputeClient = azureComputeClient;
        _hetznerCloudClient = hetznerCloudClient;
        _vultrClient = vultrClient;
        _cloudAuditService = cloudAuditService;
        _awsCredentialResolver = awsCredentialResolver;
        _digitalOceanTokenResolver = digitalOceanTokenResolver;
        _vultrTokenResolver = vultrTokenResolver;
        _gcpCredentialResolver = gcpCredentialResolver;
        _azureCredentialResolver = azureCredentialResolver;
    }

    public async Task<IReadOnlyList<CloudProviderConnectionDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.CloudProviderConnections
            .AsNoTracking()
            .OrderByDescending(connection => connection.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<CloudProviderConnectionDto> CreateAsync(
        CreateCloudProviderConnectionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var label = (request.Label ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = request.Provider switch
            {
                CloudProvider.DigitalOcean => "DigitalOcean",
                CloudProvider.Aws => "AWS",
                CloudProvider.Gcp => "Google Cloud",
                CloudProvider.Azure => "Azure",
                CloudProvider.Hetzner => "Hetzner Cloud",
                CloudProvider.Vultr => "Vultr",
                _ => "Cloud account",
            };
        }

        if (label.Length > 100)
        {
            throw new ArgumentException("Label must be 100 characters or fewer.");
        }

        string protectedCredentials;
        var defaultRegion = (request.DefaultRegion ?? string.Empty).Trim();
        var defaultProjectId = string.Empty;
        var accountHint = string.Empty;

        switch (request.Provider)
        {
            case CloudProvider.DigitalOcean:
            {
                var token = (request.AccessToken ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new ArgumentException("Access token is required.");
                }

                await _digitalOceanClient.ValidateTokenAsync(token, cancellationToken);
                protectedCredentials = CloudProviderCredentialStore.ProtectDigitalOceanToken(_secretProtector, token);
                break;
            }
            case CloudProvider.Aws:
            {
                var accessKeyId = (request.AccessKeyId ?? string.Empty).Trim();
                var secretAccessKey = (request.SecretAccessKey ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
                {
                    throw new ArgumentException("AWS access key ID and secret access key are required.");
                }

                await _awsEc2Client.ValidateCredentialsAsync(
                    new AwsRuntimeCredentials
                    {
                        AccessKeyId = accessKeyId,
                        SecretAccessKey = secretAccessKey,
                    },
                    cancellationToken);
                protectedCredentials = CloudProviderCredentialStore.ProtectAwsCredentials(
                    _secretProtector,
                    new CloudProviderCredentialStore.AwsCredentials(accessKeyId, secretAccessKey));
                break;
            }
            case CloudProvider.Gcp:
            {
                var serviceAccountJson = (request.ServiceAccountJson ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(serviceAccountJson))
                {
                    throw new ArgumentException("GCP service account JSON is required.");
                }

                await _gcpComputeClient.ValidateServiceAccountJsonAsync(serviceAccountJson, cancellationToken);
                protectedCredentials = CloudProviderCredentialStore.ProtectGcpServiceAccountJson(
                    _secretProtector,
                    serviceAccountJson);
                defaultProjectId = GcpComputeClient.ExtractProjectId(serviceAccountJson);
                break;
            }
            case CloudProvider.Azure:
            {
                var tenantId = (request.AzureTenantId ?? string.Empty).Trim();
                var clientId = (request.AzureClientId ?? string.Empty).Trim();
                var clientSecret = (request.AzureClientSecret ?? string.Empty).Trim();
                var subscriptionId = (request.AzureSubscriptionId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(tenantId)
                    || string.IsNullOrWhiteSpace(clientId)
                    || string.IsNullOrWhiteSpace(clientSecret)
                    || string.IsNullOrWhiteSpace(subscriptionId))
                {
                    throw new ArgumentException(
                        "Azure tenant ID, client ID, client secret, and subscription ID are required.");
                }

                var azureCredentials = new AzureComputeClient.AzureCredentials
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    ClientSecret = clientSecret,
                    SubscriptionId = subscriptionId,
                };
                await _azureComputeClient.ValidateCredentialsAsync(azureCredentials, cancellationToken);
                protectedCredentials = CloudProviderCredentialStore.ProtectAzureCredentials(
                    _secretProtector,
                    new CloudProviderCredentialStore.AzureCredentials(
                        tenantId,
                        clientId,
                        clientSecret,
                        subscriptionId));
                defaultProjectId = subscriptionId;
                break;
            }
            case CloudProvider.Hetzner:
            case CloudProvider.Vultr:
            {
                var token = (request.AccessToken ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new ArgumentException("API token is required.");
                }

                if (request.Provider == CloudProvider.Hetzner)
                {
                    await _hetznerCloudClient.ValidateTokenAsync(token, cancellationToken);
                    await _hetznerCloudClient.ProbeWriteAccessAsync(token, cancellationToken);
                    accountHint = HetznerCloudClient.MaskToken(token);
                }
                else
                {
                    await _vultrClient.ValidateTokenAsync(token, cancellationToken);
                }

                protectedCredentials = CloudProviderCredentialStore.ProtectApiToken(_secretProtector, token);
                break;
            }
            default:
                throw new ArgumentException($"{request.Provider} connections are not supported yet.");
        }

        var entity = new CloudProviderConnectionEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = request.Provider.ToString(),
            Label = label,
            ProtectedCredentials = protectedCredentials,
            DefaultRegion = defaultRegion,
            DefaultProjectId = defaultProjectId,
            CreatedAtUtc = DateTime.UtcNow,
            AuthMethod = CloudAuthMethod.Manual.ToString(),
            AccountHint = accountHint,
            NeedsReauth = false,
        };

        _dbContext.CloudProviderConnections.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.ConnectionCreated,
                ResourceType = "connection",
                ResourceId = entity.Id,
                Summary = $"Linked {entity.Provider} account \"{entity.Label}\".",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    provider = entity.Provider,
                    label = entity.Label,
                    defaultRegion = entity.DefaultRegion,
                }),
            },
            cancellationToken);

        return ToDto(entity);
    }

    public async Task<CloudProviderConnectionDto> UpsertOAuthConnectionAsync(
        UpsertCloudOAuthConnectionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProtectedCredentials))
        {
            throw new ArgumentException("OAuth credentials are required.");
        }

        var label = string.IsNullOrWhiteSpace(request.Label)
            ? request.Provider.ToString()
            : request.Label.Trim();
        if (label.Length > 100)
        {
            throw new ArgumentException("Label must be 100 characters or fewer.");
        }

        var accountHint = (request.AccountHint ?? string.Empty).Trim();
        if (accountHint.Length > 256)
        {
            accountHint = accountHint[..256];
        }

        CloudProviderConnectionEntity entity;
        var reconnectId = (request.ReconnectConnectionId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(reconnectId))
        {
            entity = await _dbContext.CloudProviderConnections
                         .FirstOrDefaultAsync(connection => connection.Id == reconnectId, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");
            if (!string.Equals(entity.Provider, request.Provider.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Reconnect target belongs to a different cloud provider.");
            }

            entity.Label = label;
            entity.ProtectedCredentials = request.ProtectedCredentials;
            entity.AccountHint = accountHint;
            entity.TokenExpiresAtUtc = request.TokenExpiresAtUtc;
            entity.NeedsReauth = false;
            entity.AuthMethod = request.AuthMethod.ToString();
            if (!string.IsNullOrWhiteSpace(request.DefaultRegion))
            {
                entity.DefaultRegion = request.DefaultRegion.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.DefaultProjectId))
            {
                entity.DefaultProjectId = request.DefaultProjectId.Trim();
            }
        }
        else
        {
            entity = new CloudProviderConnectionEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Provider = request.Provider.ToString(),
                Label = label,
                ProtectedCredentials = request.ProtectedCredentials,
                DefaultRegion = (request.DefaultRegion ?? string.Empty).Trim(),
                DefaultProjectId = (request.DefaultProjectId ?? string.Empty).Trim(),
                CreatedAtUtc = DateTime.UtcNow,
                AuthMethod = request.AuthMethod.ToString(),
                AccountHint = accountHint,
                TokenExpiresAtUtc = request.TokenExpiresAtUtc,
                NeedsReauth = false,
            };
            _dbContext.CloudProviderConnections.Add(entity);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = request.AuthMethod == CloudAuthMethod.AssumedRole
                    ? CloudAuditEventTypes.ConnectionAssumedRoleLinked
                    : CloudAuditEventTypes.ConnectionOAuthLinked,
                ResourceType = "connection",
                ResourceId = entity.Id,
                Summary = request.AuthMethod == CloudAuthMethod.AssumedRole
                    ? $"Connected {entity.Provider} account \"{entity.Label}\" with an IAM role."
                    : $"Signed in to {entity.Provider} as \"{entity.Label}\".",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    provider = entity.Provider,
                    label = entity.Label,
                    accountHint,
                    reconnect = !string.IsNullOrWhiteSpace(reconnectId),
                }),
            },
            cancellationToken);

        return ToDto(entity);
    }

    public async Task<CloudProviderConnectionDto> SetDefaultProjectAsync(
        string id,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections
                         .FirstOrDefaultAsync(connection => connection.Id == id, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");

        if (!string.Equals(entity.Provider, CloudProvider.Gcp.ToString(), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.Provider, CloudProvider.Azure.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Project or subscription selection is only available for Google Cloud and Azure connections.");
        }

        var selected = (projectId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selected) || selected.Length > 64)
        {
            throw new ArgumentException("Project or subscription id must be 1-64 characters.");
        }

        if (string.Equals(entity.Provider, CloudProvider.Azure.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var azureAccess = await _azureCredentialResolver.ResolveAsync(entity, cancellationToken);
            var azureScoped = new AzureComputeClient.AzureAccess
            {
                Credential = azureAccess.Credential,
                SubscriptionId = selected,
                TenantId = azureAccess.TenantId,
                AccessToken = azureAccess.AccessToken,
            };
            await _azureComputeClient.ValidateAccessAsync(azureScoped, cancellationToken);
            entity.DefaultProjectId = selected;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _cloudAuditService.WriteAsync(
                new WriteCloudAuditLogRequestDto
                {
                    EventType = CloudAuditEventTypes.ConnectionOAuthLinked,
                    ResourceType = "connection",
                    ResourceId = entity.Id,
                    Summary = $"Selected Azure subscription {selected} for \"{entity.Label}\".",
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        provider = entity.Provider,
                        subscriptionId = selected,
                    }),
                },
                cancellationToken);

            return ToDto(entity);
        }

        var gcpAccess = await _gcpCredentialResolver.ResolveAsync(entity, cancellationToken);
        var gcpScoped = new GcpComputeClient.GcpAccess
        {
            Credential = gcpAccess.Credential,
            ProjectId = selected,
            AccessToken = gcpAccess.AccessToken,
        };
        await _gcpComputeClient.ValidateAccessAsync(gcpScoped, cancellationToken);
        entity.DefaultProjectId = selected;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.ConnectionOAuthLinked,
                ResourceType = "connection",
                ResourceId = entity.Id,
                Summary = $"Selected Google Cloud project {selected} for \"{entity.Label}\".",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    provider = entity.Provider,
                    projectId = selected,
                }),
            },
            cancellationToken);

        return ToDto(entity);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");

        _dbContext.CloudProviderConnections.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _cloudAuditService.WriteAsync(
            new WriteCloudAuditLogRequestDto
            {
                EventType = CloudAuditEventTypes.ConnectionDeleted,
                ResourceType = "connection",
                ResourceId = entity.Id,
                Summary = $"Unlinked {entity.Provider} account \"{entity.Label}\".",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    provider = entity.Provider,
                    label = entity.Label,
                }),
            },
            cancellationToken);
    }

    public async Task<CloudConnectionVerifyResultDto> VerifyAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections
                         .FirstOrDefaultAsync(connection => connection.Id == id, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");

        if (!Enum.TryParse<CloudProvider>(entity.Provider, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException("Unknown cloud provider on this connection.");
        }

        try
        {
            await ValidateStoredCredentialsAsync(entity, provider, cancellationToken);
            entity.NeedsReauth = false;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var dto = ToDto(entity);
            await _cloudAuditService.WriteAsync(
                new WriteCloudAuditLogRequestDto
                {
                    EventType = CloudAuditEventTypes.ConnectionVerified,
                    ResourceType = "connection",
                    ResourceId = entity.Id,
                    Summary = $"Verified {entity.Provider} account \"{entity.Label}\".",
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        provider = entity.Provider,
                        label = entity.Label,
                        ok = true,
                    }),
                },
                cancellationToken);

            return new CloudConnectionVerifyResultDto
            {
                Ok = true,
                Message = $"{provider} credentials are valid and the API is reachable.",
                Connection = dto,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not KeyNotFoundException)
        {
            entity.NeedsReauth = true;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var dto = ToDto(entity);
            await _cloudAuditService.WriteAsync(
                new WriteCloudAuditLogRequestDto
                {
                    EventType = CloudAuditEventTypes.ConnectionVerified,
                    ResourceType = "connection",
                    ResourceId = entity.Id,
                    Summary = $"Verify failed for {entity.Provider} account \"{entity.Label}\".",
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        provider = entity.Provider,
                        label = entity.Label,
                        ok = false,
                    }),
                },
                cancellationToken);

            return new CloudConnectionVerifyResultDto
            {
                Ok = false,
                Message = string.IsNullOrWhiteSpace(ex.Message)
                    ? $"{provider} rejected the stored credentials."
                    : ex.Message,
                Connection = dto,
            };
        }
    }

    public async Task<IReadOnlyList<CloudInstanceDto>> ListInstancesAsync(
        string connectionId,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.CloudProviderConnections.AsNoTracking()
                         .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken)
                     ?? throw new KeyNotFoundException("Cloud connection not found.");

        if (!Enum.TryParse<CloudProvider>(entity.Provider, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException("Unknown cloud provider on this connection.");
        }

        var regionFilter = (region ?? entity.DefaultRegion ?? string.Empty).Trim();

        return provider switch
        {
            CloudProvider.DigitalOcean => await ListDigitalOceanInstancesAsync(
                entity,
                regionFilter,
                cancellationToken),
            CloudProvider.Aws => await ListAwsInstancesAsync(
                entity,
                regionFilter,
                cancellationToken),
            CloudProvider.Gcp => await ListGcpInstancesAsync(
                entity,
                regionFilter,
                cancellationToken),
            CloudProvider.Azure => await ListAzureInstancesAsync(
                entity,
                regionFilter,
                cancellationToken),
            CloudProvider.Hetzner => await ListHetznerInstancesAsync(
                entity,
                regionFilter,
                cancellationToken),
            CloudProvider.Vultr => await ListVultrInstancesAsync(
                entity,
                regionFilter,
                cancellationToken),
            _ => throw new InvalidOperationException($"{provider} instance listing is not supported yet."),
        };
    }

    private async Task ValidateStoredCredentialsAsync(
        CloudProviderConnectionEntity entity,
        CloudProvider provider,
        CancellationToken cancellationToken)
    {
        switch (provider)
        {
            case CloudProvider.DigitalOcean:
            {
                var accessToken = await _digitalOceanTokenResolver.ResolveAsync(entity, cancellationToken);
                await _digitalOceanClient.ValidateTokenAsync(accessToken, cancellationToken);
                return;
            }
            case CloudProvider.Aws:
            {
                var credentials = await _awsCredentialResolver.ResolveAsync(entity, cancellationToken);
                await _awsEc2Client.ValidateCredentialsAsync(credentials, cancellationToken);
                return;
            }
            case CloudProvider.Gcp:
            {
                var access = await _gcpCredentialResolver.ResolveAsync(entity, cancellationToken);
                if (string.IsNullOrWhiteSpace(access.ProjectId))
                {
                    _ = await _gcpComputeClient.ListProjectsAsync(access, cancellationToken);
                    return;
                }

                await _gcpComputeClient.ValidateAccessAsync(access, cancellationToken);
                return;
            }
            case CloudProvider.Azure:
            {
                var access = await _azureCredentialResolver.ResolveAsync(entity, cancellationToken);
                if (string.IsNullOrWhiteSpace(access.SubscriptionId))
                {
                    _ = await _azureComputeClient.ListSubscriptionsAsync(access, cancellationToken);
                    return;
                }

                await _azureComputeClient.ValidateAccessAsync(access, cancellationToken);
                return;
            }
            case CloudProvider.Hetzner:
            {
                var accessToken = CloudProviderCredentialStore.UnprotectApiToken(
                    _secretProtector,
                    entity.ProtectedCredentials);
                await _hetznerCloudClient.ValidateTokenAsync(accessToken, cancellationToken);
                return;
            }
            case CloudProvider.Vultr:
            {
                var accessToken = await _vultrTokenResolver.ResolveAsync(entity, cancellationToken);
                await _vultrClient.ValidateTokenAsync(accessToken, cancellationToken);
                return;
            }
            default:
                throw new InvalidOperationException($"{provider} credential verification is not supported yet.");
        }
    }

    private async Task<IReadOnlyList<CloudInstanceDto>> ListDigitalOceanInstancesAsync(
        CloudProviderConnectionEntity entity,
        string regionFilter,
        CancellationToken cancellationToken)
    {
        var accessToken = await _digitalOceanTokenResolver.ResolveAsync(entity, cancellationToken);

        var droplets = await _digitalOceanClient.ListDropletsAsync(accessToken, cancellationToken);

        return droplets
            .Where(droplet => string.IsNullOrWhiteSpace(regionFilter)
                              || string.Equals(droplet.Region?.Slug, regionFilter, StringComparison.OrdinalIgnoreCase))
            .Select(droplet =>
            {
                var publicIp = droplet.Networks?.V4
                    .FirstOrDefault(network => string.Equals(network.Type, "public", StringComparison.OrdinalIgnoreCase))
                    ?.IpAddress ?? string.Empty;

                var imageSlug = droplet.Image?.Slug ?? string.Empty;
                return new CloudInstanceDto
                {
                    Id = droplet.Id.ToString(),
                    Provider = CloudProvider.DigitalOcean,
                    Name = droplet.Name,
                    Region = droplet.Region?.Slug ?? string.Empty,
                    State = droplet.Status,
                    PublicHost = publicIp,
                    SuggestedSshUser = SuggestSshUserFromImage(imageSlug, droplet.Image?.Distribution),
                    Image = string.IsNullOrWhiteSpace(imageSlug) ? droplet.Image?.Distribution ?? string.Empty : imageSlug,
                    InstanceType = droplet.SizeSlug ?? string.Empty,
                };
            })
            .Where(instance => !string.IsNullOrWhiteSpace(instance.PublicHost))
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<CloudInstanceDto>> ListAwsInstancesAsync(
        CloudProviderConnectionEntity entity,
        string regionFilter,
        CancellationToken cancellationToken)
    {
        var credentials = await _awsCredentialResolver.ResolveAsync(entity, cancellationToken);

        var instances = await _awsEc2Client.ListRunningInstancesAsync(
            credentials,
            string.IsNullOrWhiteSpace(regionFilter) ? null : regionFilter,
            cancellationToken);

        return instances
            .Select(instance => new CloudInstanceDto
            {
                Id = instance.Id,
                Provider = CloudProvider.Aws,
                Name = instance.Name,
                Region = instance.Region,
                State = instance.State,
                PublicHost = instance.PublicHost,
                SuggestedSshUser = instance.SuggestedSshUser,
                Image = string.IsNullOrWhiteSpace(instance.InstanceType)
                    ? instance.Image
                    : $"{instance.Image} ({instance.InstanceType})",
                InstanceType = instance.InstanceType,
            })
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<CloudInstanceDto>> ListGcpInstancesAsync(
        CloudProviderConnectionEntity entity,
        string regionFilter,
        CancellationToken cancellationToken)
    {
        var access = await _gcpCredentialResolver.ResolveAsync(entity, cancellationToken);
        var instances = await _gcpComputeClient.ListRunningInstancesAsync(
            access,
            string.IsNullOrWhiteSpace(regionFilter) ? null : regionFilter,
            cancellationToken);

        return instances
            .Select(instance => new CloudInstanceDto
            {
                Id = instance.Id,
                Provider = CloudProvider.Gcp,
                Name = instance.Name,
                Region = instance.Zone,
                State = instance.State,
                PublicHost = instance.PublicHost,
                SuggestedSshUser = instance.SuggestedSshUser,
                Image = instance.Image,
                InstanceType = instance.MachineType,
            })
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<CloudInstanceDto>> ListAzureInstancesAsync(
        CloudProviderConnectionEntity entity,
        string locationFilter,
        CancellationToken cancellationToken)
    {
        var access = await _azureCredentialResolver.ResolveAsync(entity, cancellationToken);
        var instances = await _azureComputeClient.ListRunningInstancesAsync(
            access,
            string.IsNullOrWhiteSpace(locationFilter) ? null : locationFilter,
            cancellationToken);

        return instances
            .Select(instance => new CloudInstanceDto
            {
                Id = instance.Id,
                Provider = CloudProvider.Azure,
                Name = instance.Name,
                Region = instance.Location,
                State = "running",
                PublicHost = instance.PublicHost,
                SuggestedSshUser = instance.SuggestedSshUser,
                Image = string.IsNullOrWhiteSpace(instance.Image)
                    ? instance.ResourceGroup
                    : $"{instance.Image} ({instance.ResourceGroup})",
                InstanceType = instance.VmSize,
            })
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<CloudInstanceDto>> ListHetznerInstancesAsync(
        CloudProviderConnectionEntity entity,
        string locationFilter,
        CancellationToken cancellationToken)
    {
        var accessToken = CloudProviderCredentialStore.UnprotectApiToken(
            _secretProtector,
            entity.ProtectedCredentials);

        var servers = await _hetznerCloudClient.ListServersAsync(
            accessToken,
            string.IsNullOrWhiteSpace(locationFilter) ? null : locationFilter,
            cancellationToken);

        return servers
            .Select(server => new CloudInstanceDto
            {
                Id = server.Id.ToString(),
                Provider = CloudProvider.Hetzner,
                Name = server.Name,
                Region = server.Datacenter?.Location?.Name ?? server.Datacenter?.Name ?? string.Empty,
                State = server.Status,
                PublicHost = server.PublicIpv4,
                SuggestedSshUser = server.SuggestedSshUser,
                Image = server.Image?.Name ?? server.Image?.Description ?? string.Empty,
                InstanceType = server.ServerType ?? string.Empty,
            })
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<CloudInstanceDto>> ListVultrInstancesAsync(
        CloudProviderConnectionEntity entity,
        string regionFilter,
        CancellationToken cancellationToken)
    {
        var accessToken = await _vultrTokenResolver.ResolveAsync(entity, cancellationToken);

        var instances = await _vultrClient.ListInstancesAsync(
            accessToken,
            string.IsNullOrWhiteSpace(regionFilter) ? null : regionFilter,
            cancellationToken);

        return instances
            .Select(instance => new CloudInstanceDto
            {
                Id = instance.Id,
                Provider = CloudProvider.Vultr,
                Name = instance.Label,
                Region = instance.Region,
                State = instance.Status,
                PublicHost = instance.PublicHost,
                SuggestedSshUser = instance.SuggestedSshUser,
                Image = instance.Os,
                InstanceType = instance.Plan ?? string.Empty,
            })
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static AzureComputeClient.AzureCredentials ToAzureClientCredentials(
        CloudProviderCredentialStore.AzureCredentials credentials)
        => new()
        {
            TenantId = credentials.TenantId,
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            SubscriptionId = credentials.SubscriptionId,
        };

    internal static string SuggestSshUserFromImage(string imageSlug, string? distribution)
    {
        var slug = (imageSlug ?? string.Empty).ToLowerInvariant();
        if (slug.StartsWith("ubuntu", StringComparison.Ordinal))
        {
            return "ubuntu";
        }

        if (slug.StartsWith("debian", StringComparison.Ordinal))
        {
            return "debian";
        }

        if (slug.StartsWith("fedora", StringComparison.Ordinal))
        {
            return "fedora";
        }

        var dist = (distribution ?? string.Empty).ToLowerInvariant();
        if (dist.Contains("ubuntu", StringComparison.Ordinal))
        {
            return "ubuntu";
        }

        if (dist.Contains("debian", StringComparison.Ordinal))
        {
            return "debian";
        }

        return "root";
    }

    private static CloudProviderConnectionDto ToDto(CloudProviderConnectionEntity entity)
    {
        Enum.TryParse<CloudProvider>(entity.Provider, ignoreCase: true, out var provider);
        var authMethod = Enum.TryParse<CloudAuthMethod>(entity.AuthMethod, ignoreCase: true, out var parsedMethod)
            ? parsedMethod
            : CloudAuthMethod.Manual;
        return new CloudProviderConnectionDto
        {
            Id = entity.Id,
            Provider = provider,
            Label = entity.Label,
            DefaultRegion = string.IsNullOrWhiteSpace(entity.DefaultRegion) ? null : entity.DefaultRegion,
            DefaultProjectId = string.IsNullOrWhiteSpace(entity.DefaultProjectId) ? null : entity.DefaultProjectId,
            CreatedAtUtc = entity.CreatedAtUtc,
            AuthMethod = authMethod,
            AccountHint = string.IsNullOrWhiteSpace(entity.AccountHint) ? null : entity.AccountHint,
            TokenExpiresAtUtc = entity.TokenExpiresAtUtc,
            NeedsReauth = entity.NeedsReauth,
        };
    }
}
