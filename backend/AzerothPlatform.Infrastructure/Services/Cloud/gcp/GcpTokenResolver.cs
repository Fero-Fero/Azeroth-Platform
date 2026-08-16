using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public interface IGcpCredentialResolver
{
    Task<GcpComputeClient.GcpAccess> ResolveAsync(
        CloudProviderConnectionEntity entity,
        CancellationToken cancellationToken = default);
}

public sealed class GcpTokenResolver : IGcpCredentialResolver
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly AzerothCoreDbContext _dbContext;
    private readonly ISecretProtector _secretProtector;
    private readonly GcpComputeClient _gcpComputeClient;
    private readonly ICloudAuditService _cloudAuditService;
    private readonly CloudOAuthOptions _options;

    public GcpTokenResolver(
        AzerothCoreDbContext dbContext,
        ISecretProtector secretProtector,
        GcpComputeClient gcpComputeClient,
        ICloudAuditService cloudAuditService,
        IOptions<CloudOAuthOptions> options)
    {
        _dbContext = dbContext;
        _secretProtector = secretProtector;
        _gcpComputeClient = gcpComputeClient;
        _cloudAuditService = cloudAuditService;
        _options = options.Value;
    }

    public async Task<GcpComputeClient.GcpAccess> ResolveAsync(
        CloudProviderConnectionEntity entity,
        CancellationToken cancellationToken = default)
    {
        var projectId = string.IsNullOrWhiteSpace(entity.DefaultProjectId)
            ? string.Empty
            : entity.DefaultProjectId.Trim();

        if (!CloudProviderCredentialStore.TryUnprotectOAuthTokens(
                _secretProtector,
                entity.ProtectedCredentials,
                out var envelope))
        {
            var json = CloudProviderCredentialStore.UnprotectGcpServiceAccountJson(
                _secretProtector,
                entity.ProtectedCredentials);
            if (string.IsNullOrWhiteSpace(projectId))
            {
                projectId = GcpComputeClient.ExtractProjectId(json);
            }

            return GcpComputeClient.FromServiceAccountJson(json, projectId);
        }

        var expires = envelope.ExpiresAtUtc ?? entity.TokenExpiresAtUtc;
        if (expires is { } expiry && expiry - RefreshSkew > DateTime.UtcNow)
        {
            return GcpComputeClient.FromAccessToken(envelope.AccessToken, projectId);
        }

        if (string.IsNullOrWhiteSpace(envelope.RefreshToken) || !_options.Gcp.IsConfigured)
        {
            if (string.IsNullOrWhiteSpace(envelope.AccessToken))
            {
                throw new InvalidOperationException(
                    "Google Cloud login expired. Reconnect the account from Cloud settings.");
            }

            return GcpComputeClient.FromAccessToken(envelope.AccessToken, projectId);
        }

        try
        {
            var refreshed = await _gcpComputeClient.RefreshAccessTokenAsync(
                _options.Gcp.ClientId,
                _options.Gcp.ClientSecret,
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
                    Summary = $"Refreshed Google Cloud login for \"{tracked.Label}\".",
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        provider = tracked.Provider,
                        expiresAtUtc = tracked.TokenExpiresAtUtc,
                    }),
                },
                cancellationToken);

            return GcpComputeClient.FromAccessToken(envelope.AccessToken, projectId);
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
                "Google Cloud login expired. Reconnect the account from Cloud settings.",
                ex);
        }
    }
}
