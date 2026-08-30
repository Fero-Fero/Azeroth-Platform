using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public interface IDigitalOceanTokenResolver
{
    Task<string> ResolveAsync(
        CloudProviderConnectionEntity entity,
        CancellationToken cancellationToken = default);
}

public sealed class DigitalOceanTokenResolver : IDigitalOceanTokenResolver
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly AzerothCoreDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;
    private readonly DigitalOceanClient _digitalOceanClient;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly CloudOAuthOptions _options;

    public DigitalOceanTokenResolver(
        AzerothCoreDbContext dbContext,
        ISecretProtector secretProtector,
        DigitalOceanClient digitalOceanClient,
        ICloudAuditService cloudAuditService,
        IOptions<CloudOAuthOptions> options)
    {
        _dbContext = dbContext;
        _secretProtector = secretProtector;
        _digitalOceanClient = digitalOceanClient;
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
            return CloudProviderCredentialStore.UnprotectDigitalOceanToken(
                _secretProtector,
                entity.ProtectedCredentials);
        }

        var expires = envelope.ExpiresAtUtc ?? entity.TokenExpiresAtUtc;
        if (expires is { } expiry && expiry - RefreshSkew > DateTime.UtcNow)
        {
            return envelope.AccessToken.Trim();
        }

        if (string.IsNullOrWhiteSpace(envelope.RefreshToken) || !_options.DigitalOcean.IsConfigured)
        {
            return envelope.AccessToken.Trim();
        }

        try
        {
            var refreshed = await _digitalOceanClient.RefreshAccessTokenAsync(
                _options.DigitalOcean.ClientId,
                _options.DigitalOcean.ClientSecret,
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
                : envelope.ExpiresAtUtc;

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
                    Summary = $"Refreshed DigitalOcean login for \"{tracked.Label}\".",
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
                "DigitalOcean login expired. Reconnect the account from Cloud settings.",
                ex);
        }
    }
}
