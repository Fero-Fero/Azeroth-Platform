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
        CancellationToken cancellationToken = default,
        bool skipModuleCheck = false);

    Task<BuildStatusDto?> GetStatusAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks in-progress builds as failed after a manager restart and clears stuck <see cref="StackStatus.Building"/> rows.
    /// </summary>
    Task RecoverInterruptedBuildsAsync(CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(string stackId, CancellationToken cancellationToken = default);

    Task<long> CleanupAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clones missing git modules or resets existing checkouts to the latest commit on the
    /// per-stack branch override (catalog branch when none is set).
    /// Does not start a Docker build. Package modules are skipped.
    /// </summary>
    Task<SyncStackModulesResultDto> SyncModulesAsync(
        string stackId,
        string? moduleId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clones/refreshes core and modules, then compiles each selected module CMake target against the core.
    /// Does not build Docker images.
    /// </summary>
    Task<BuildStatusDto> CheckModulesAsync(string stackId, CancellationToken cancellationToken = default);
}
