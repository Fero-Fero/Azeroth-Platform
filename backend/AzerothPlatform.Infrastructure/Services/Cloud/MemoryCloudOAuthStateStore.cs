using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace AzerothPlatform.Infrastructure.Services.Cloud;

public sealed class MemoryCloudOAuthStateStore : ICloudOAuthStateStore
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache;

    public MemoryCloudOAuthStateStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public Task<CloudOAuthStateDto> CreateAsync(
        CloudProvider provider,
        string? codeVerifier,
        string? returnUrl,
        string? reconnectConnectionId,
        string? label,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = new CloudOAuthStateDto
        {
            State = CloudOAuthPkce.CreateState(),
            Provider = provider,
            CodeVerifier = string.IsNullOrWhiteSpace(codeVerifier) ? null : codeVerifier,
            ReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? null : returnUrl.Trim(),
            ReconnectConnectionId = string.IsNullOrWhiteSpace(reconnectConnectionId)
                ? null
                : reconnectConnectionId.Trim(),
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
        };

        _cache.Set(CacheKey(payload.State), payload, DefaultTtl);
        return Task.FromResult(payload);
    }

    public Task<CloudOAuthStateDto?> TakeAsync(string state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(state))
        {
            return Task.FromResult<CloudOAuthStateDto?>(null);
        }

        var key = CacheKey(state.Trim());
        if (!_cache.TryGetValue(key, out CloudOAuthStateDto? payload))
        {
            return Task.FromResult<CloudOAuthStateDto?>(null);
        }

        _cache.Remove(key);
        return Task.FromResult(payload);
    }

    internal static string CacheKey(string state) => $"cloud-oauth-state:{state}";
}