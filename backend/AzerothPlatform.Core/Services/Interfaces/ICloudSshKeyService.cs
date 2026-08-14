using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudSshKeyService
{
    Task<IReadOnlyList<CloudSshKeyDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<CloudSshKeyDto> CreateAsync(CreateCloudSshKeyRequestDto request, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Decrypts a saved key for SSH use. Never expose through list/get DTOs.</summary>
    Task<string> ResolvePrivateKeyAsync(
        string id,
        string? usageContext = null,
        CancellationToken cancellationToken = default);
}
