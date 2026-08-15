using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudAuthOrchestrator
{
    IReadOnlyList<CloudAuthProviderStatusDto> ListProviderStatus();

    CloudAuthProviderStatusDto GetProviderStatus(CloudProvider provider);

    Task<CloudAuthStartResultDto> StartAsync(
        CloudProvider provider,
        CloudAuthStartRequestDto request,
        CancellationToken cancellationToken = default);

    Task<CloudProviderConnectionDto> HandleCallbackAsync(
        CloudProvider provider,
        string? code,
        string? state,
        string? error,
        string? errorDescription,
        CancellationToken cancellationToken = default);

    Task<CloudProviderConnectionDto> CompleteAsync(
        CloudProvider provider,
        CloudAuthCompleteRequestDto request,
        CancellationToken cancellationToken = default);

    Task RefreshAsync(
        CloudProvider provider,
        string connectionId,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(string connectionId, CancellationToken cancellationToken = default);
}
