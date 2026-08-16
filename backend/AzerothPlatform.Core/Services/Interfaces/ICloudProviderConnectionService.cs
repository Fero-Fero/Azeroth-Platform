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

    Task<CloudProviderConnectionDto> SetDefaultProjectAsync(
        string id,
        string projectId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-validates stored credentials against the provider API (same checks used at link time).
    /// </summary>
    Task<CloudConnectionVerifyResultDto> VerifyAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudInstanceDto>> ListInstancesAsync(
        string connectionId,
        string? region = null,
        CancellationToken cancellationToken = default);
}
