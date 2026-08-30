using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public interface IVultrTokenResolver
{
    Task<string> ResolveAsync(
        CloudProviderConnectionEntity entity,
        CancellationToken cancellationToken = default);
}

public sealed class VultrTokenResolver : IVultrTokenResolver
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(10);

    private readonly AzerothCoreDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;
    private readonly VultrClient _vultrClient;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly CloudOAuthOptions _options;

    public VultrTokenResolver(
        AzerothCoreDbContext dbContext,
        ISecretProtector secretProtector,
        VultrClient vultrClient,
        ICloudAuditService cloudAuditService,
        IOptions<CloudOAuthOptions> options)
    {
        _dbContext = dbContext;
        _secretProtector = secretProtector;
        _vultrClient = vultrClient;
        _cloudAuditService = cloudAuditService;
        _options = options.Value;
    }

    public async Task<string> ResolveAsync(
        CloudProviderConnectionEntity entity,
        CancellationToken cancellationToken = default)
    {
        if (!CloudProviderCredentialStore.TryUnprotectOAuthTokens(
                _secretProtector,
                entity.ProtectedCredentials,
                out var envelope))
        {
            return CloudProviderCredentialStore.UnprotectApiToken(
                _secretProtector,
                entity.ProtectedCredentials);
        }

        var expires = envelope.ExpiresAtUtc ?? entity.TokenExpiresAtUtc;
        var stillFresh = expires is { } expiry && expiry - RefreshSkew > DateTime.UtcNow;
        if (stillFresh)
        {
            return envelope.AccessToken.Trim();
        }

        if (string.IsNullOrWhiteSpace(envelope.RefreshToken) || !_options.Vultr.IsVultrOAuthConfigured)
        {
            return envelope.AccessToken.Trim();
        }

        try
        {
            var refreshed = await _vultrClient.RefreshAccessTokenAsync(
                _options.Vultr.ProviderId,
                _options.Vultr.ClientId,
                _options.Vultr.ClientSecret,
                envelope.RefreshToken,
                cancellationToken);

            envelope.AccessToken = refreshed.AccessToken.Trim();
            if (string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            {
                throw new InvalidOperationException("Vultr did not return a rotated refresh token.");
            }

            envelope.RefreshToken = refreshed.RefreshToken.Trim();
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
                    Summary = $"Refreshed Vultr login for \"{tracked.Label}\".",
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        provider = tracked.Provider,
                        expiresAtUtc = tracked.TokenExpiresAtUtc,
                    }),
                },
                cancellationToken);

            return envelope.AccessToken;
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
                "Vultr login expired. Reconnect the account from Cloud settings.",
                ex);
        }
    }
}
