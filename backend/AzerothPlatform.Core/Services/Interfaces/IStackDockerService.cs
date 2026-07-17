using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>Inspects and cleans up per-stack Docker images, volumes, and on-disk build artifacts.</summary>
public interface IStackDockerService
{
    Task<DockerDiskUsageDto> GetDiskUsageAsync(CancellationToken cancellationToken = default);

    Task<StackDockerOverviewDto?> GetOverviewAsync(string stackId, CancellationToken cancellationToken = default);

    Task<DockerCleanupResultDto> CleanupUnusedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes old build leftovers (dangling layers, unused stack images, orphaned checkouts) without
    /// pruning the Docker build cache.
    /// </summary>
    Task<DockerCleanupResultDto> CleanupOldBuildsAsync(CancellationToken cancellationToken = default);

    Task<StackDockerDeleteResultDto> DeleteBuildFilesAsync(string stackId, CancellationToken cancellationToken = default);

    Task<StackDockerDeleteResultDto> DeleteImageAsync(string stackId, string imageId, CancellationToken cancellationToken = default);

    Task<StackDockerDeleteResultDto> DeleteVolumeAsync(string stackId, string volumeName, CancellationToken cancellationToken = default);

    Task<DockerVolumeAuditDto?> GetVolumeAuditAsync(string stackId, CancellationToken cancellationToken = default);

    Task<DockerVolumeCleanupResultDto> CleanupVolumeAuditAsync(
        string stackId,
        DockerVolumeCleanupRequestDto request,
        CancellationToken cancellationToken = default);
}
