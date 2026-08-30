using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudOAuthStateStore
{
    Task<CloudOAuthStateDto> CreateAsync(
        CloudProvider provider,
        string? codeVerifier,
        string? returnUrl,
        string? reconnectConnectionId,
        string? label,
        CancellationToken cancellationToken = default);

    Task<CloudOAuthStateDto?> TakeAsync(string state, CancellationToken cancellationToken = default);
}
