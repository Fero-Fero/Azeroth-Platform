using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Service for orchestrating AzerothCore builds
/// </summary>
public interface IBuildService
{
    Task<BuildStatusDto> StartAsync(
        string stackId,
        StackConfigurationDto? configuration = null,
        CancellationToken cancellationToken = default);

    Task<BuildStatusDto?> GetStatusAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks in-progress builds as failed after a manager restart and clears stuck <see cref="StackStatus.Building"/> rows.
    /// </summary>
    Task RecoverInterruptedBuildsAsync(CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(string stackId, CancellationToken cancellationToken = default);

    Task<long> CleanupAsync(string stackId, CancellationToken cancellationToken = default);
}
