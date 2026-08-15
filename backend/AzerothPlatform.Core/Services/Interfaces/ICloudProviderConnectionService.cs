using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudProviderConnectionService
{
    Task<IReadOnlyList<CloudProviderConnectionDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<CloudProviderConnectionDto> CreateAsync(
        CreateCloudProviderConnectionRequestDto request,
        CancellationToken cancellationToken = default);

    Task<CloudProviderConnectionDto> UpsertOAuthConnectionAsync(
        UpsertCloudOAuthConnectionRequestDto request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudInstanceDto>> ListInstancesAsync(
        string connectionId,
        string? region = null,
        CancellationToken cancellationToken = default);
}
