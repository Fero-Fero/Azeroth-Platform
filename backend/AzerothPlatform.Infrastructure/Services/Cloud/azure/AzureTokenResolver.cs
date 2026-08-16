using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public interface IAzureCredentialResolver
{
    Task<AzureComputeClient.AzureAccess> ResolveAsync(
        CloudProviderConnectionEntity entity,
        CancellationToken cancellationToken = default);
}

public sealed class AzureTokenResolver : IAzureCredentialResolver
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly AzerothCoreDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;
    private readonly AzureComputeClient _azureComputeClient;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly CloudOAuthOptions _options;

    public AzureTokenResolver(
        AzerothCoreDbContext dbContext,
        ISecretProtector secretProtector,
        AzureComputeClient azureComputeClient,
        ICloudAuditService cloudAuditService,
        IOptions<CloudOAuthOptions> options)
    {
        _dbContext = dbContext;
        _secretProtector = secretProtector;
        _azureComputeClient = azureComputeClient;
        _cloudAuditService = cloudAuditService;
        _options = options.Value;
    }

    public async Task<AzureComputeClient.AzureAccess> ResolveAsync(
        CloudProviderConnectionEntity entity,
        CancellationToken cancellationToken = default)
    {
        var subscriptionId = string.IsNullOrWhiteSpace(entity.DefaultProjectId)
            ? string.Empty
            : entity.DefaultProjectId.Trim();

        if (!CloudProviderCredentialStore.TryUnprotectOAuthTokens(
                _secretProtector,
                entity.ProtectedCredentials,
                out var envelope))
        {
            var credentials = CloudProviderCredentialStore.UnprotectAzureCredentials(
                _secretProtector,
                entity.ProtectedCredentials);
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                subscriptionId = credentials.SubscriptionId;
            }

            return AzureComputeClient.FromServicePrincipal(
                new AzureComputeClient.AzureCredentials
                {
                    TenantId = credentials.TenantId,
                    ClientId = credentials.ClientId,
                    ClientSecret = credentials.ClientSecret,
                    SubscriptionId = subscriptionId,
                });
        }

        var tenantId = string.IsNullOrWhiteSpace(envelope.TenantId)
            ? _options.Azure.TenantId
            : envelope.TenantId;
        var expires = envelope.ExpiresAtUtc ?? entity.TokenExpiresAtUtc ?? DateTime.UtcNow.AddMinutes(5);
        if (expires - RefreshSkew > DateTime.UtcNow)
        {
            return AzureComputeClient.FromAccessToken(
                envelope.AccessToken,
                new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc)),
                subscriptionId,
                tenantId);
        }

        if (string.IsNullOrWhiteSpace(envelope.RefreshToken) || !_options.Azure.IsConfigured)
        {
            if (string.IsNullOrWhiteSpace(envelope.AccessToken))
            {
                throw new InvalidOperationException(
                    "Microsoft sign-in expired. Reconnect the account from Cloud settings.");
            }

            return AzureComputeClient.FromAccessToken(
                envelope.AccessToken,
                new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc)),
                subscriptionId,
                tenantId);
        }

        try
        {
            var refreshed = await _azureComputeClient.RefreshAccessTokenAsync(
                AzureComputeClient.ResolveTenantId(tenantId),
                _options.Azure.ClientId,
                _options.Azure.ClientSecret,
                envelope.RefreshToken,
                cancellationToken);

            envelope.AccessToken = refreshed.AccessToken.Trim();
            if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            {
                envelope.RefreshToken = refreshed.RefreshToken.Trim();
            }

            envelope.Scope = string.IsNullOrWhiteSpace(refreshed.Scope) ? envelope.Scope : refreshed.Scope;
            envelope.ExpiresAtUtc = refreshed.ExpiresIn > 0
                ? DateTime.UtcNow.AddSeconds(refreshed.ExpiresIn)
                : DateTime.UtcNow.AddHours(1);

            var tracked = await _dbContext.CloudProviderConnections
                              .FirstOrDefaultAsync(connection => connection.Id == entity.Id, cancellationToken)
                          ?? throw new KeyNotFoundException("Cloud connection not found.");
            tracked.ProtectedCredentials = CloudProviderCredentialStore.ProtectOAuthTokens(
                _secretProtector,
                envelope);
            tracked.TokenExpiresAtUtc = envelope.ExpiresAtUtc;
            tracked.NeedsReauth = false;
            await _dbContext.SaveChangesAsync(cancellationToken);

            entity.ProtectedCredentials = tracked.ProtectedCredentials;
            entity.TokenExpiresAtUtc = tracked.TokenExpiresAtUtc;
            entity.NeedsReauth = false;

            await _cloudAuditService.WriteAsync(
                new WriteCloudAuditLogRequestDto
                {
                    EventType = CloudAuditEventTypes.ConnectionOAuthRefreshed,
                    ResourceType = "connection",
                    ResourceId = tracked.Id,
                    Summary = $"Refreshed Microsoft sign-in for \"{tracked.Label}\".",
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        provider = tracked.Provider,
                        expiresAtUtc = tracked.TokenExpiresAtUtc,
                    }),
                },
                cancellationToken);

            return AzureComputeClient.FromAccessToken(
                envelope.AccessToken,
                new DateTimeOffset(DateTime.SpecifyKind(envelope.ExpiresAtUtc ?? DateTime.UtcNow.AddHours(1), DateTimeKind.Utc)),
                subscriptionId,
                tenantId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var tracked = await _dbContext.CloudProviderConnections
                .FirstOrDefaultAsync(connection => connection.Id == entity.Id, cancellationToken);
            if (tracked is not null)
            {
                tracked.NeedsReauth = true;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            throw new InvalidOperationException(
                "Microsoft sign-in expired. Reconnect the account from Cloud settings.",
                ex);
        }
    }
}
