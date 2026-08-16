using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public interface IAwsCredentialResolver
{
    Task<AwsRuntimeCredentials> ResolveAsync(
        CloudProviderConnectionEntity entity,
        CancellationToken cancellationToken = default);

    void Invalidate(string connectionId);
}

public sealed class AwsCredentialResolver : IAwsCredentialResolver
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly ISecretProtector _secretProtector;
    private readonly AwsStsClient _awsStsClient;
    private readonly IMemoryCache _cache;

    public AwsCredentialResolver(
        ISecretProtector secretProtector,
        AwsStsClient awsStsClient,
        IMemoryCache cache)
    {
        _secretProtector = secretProtector;
        _awsStsClient = awsStsClient;
        _cache = cache;
    }

    public async Task<AwsRuntimeCredentials> ResolveAsync(
        CloudProviderConnectionEntity entity,
        CancellationToken cancellationToken = default)
    {
        var plaintext = _secretProtector.Unprotect(entity.ProtectedCredentials).Trim();
        if (CloudProviderCredentialStore.TryParseAwsAssumedRole(plaintext, out var assumedRole))
        {
            var cacheKey = CacheKey(entity.Id);
            if (_cache.TryGetValue(cacheKey, out AwsRuntimeCredentials? cached)
                && cached is not null
                && cached.ExpiresAtUtc is { } expiry
                && expiry - RefreshSkew > DateTime.UtcNow)
            {
                return cached;
            }

            var session = await _awsStsClient.AssumeRoleAsync(
                assumedRole.RoleArn,
                assumedRole.ExternalId,
                cancellationToken);

            var lifetime = session.ExpiresAtUtc is { } sessionExpiry
                ? sessionExpiry - DateTime.UtcNow - RefreshSkew
                : TimeSpan.FromMinutes(50);
            if (lifetime < TimeSpan.FromMinutes(1))
            {
                lifetime = TimeSpan.FromMinutes(1);
            }

            _cache.Set(cacheKey, session, lifetime);
            return session;
        }

        var keys = CloudProviderCredentialStore.UnprotectAwsCredentials(
            _secretProtector,
            entity.ProtectedCredentials);
        return new AwsRuntimeCredentials
        {
            AccessKeyId = keys.AccessKeyId,
            SecretAccessKey = keys.SecretAccessKey,
        };
    }

    public void Invalidate(string connectionId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            _cache.Remove(CacheKey(connectionId));
        }
    }

    internal static string CacheKey(string connectionId) => $"aws-sts-session:{connectionId}";
}
