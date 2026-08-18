using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Service for Docker operations
/// </summary>
public interface IDockerService
{
    /// <summary>
    /// Checks whether the Docker daemon is reachable.
    /// </summary>
    /// <param name="dockerContext">
    /// Optional docker context name to target a specific engine (e.g. an external stack's SSH context).
    /// Null uses the manager's default local engine.
    /// </param>
    Task<bool> IsDockerAvailableAsync(string? dockerContext = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists containers and reports whether the engine responded to <c>docker ps</c>.
    /// </summary>
    Task<DockerListContainersResult> ListContainersWithEngineStatusAsync(
        string? composeProjectName = null,
        string? dockerContext = null,
        string? nameContains = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists containers managed by Docker, optionally filtered to a specific compose stack.
    /// </summary>
    /// <param name="composeProjectName">Optional compose project filter.</param>
    /// <param name="dockerContext">
    /// Optional docker context name to target a specific engine (e.g. an external stack's SSH context).
    /// Null uses the manager's default local engine.
    /// </param>
    Task<IReadOnlyList<ContainerStatusDto>> ListContainersAsync(
        string? composeProjectName = null,
        string? dockerContext = null,
        string? nameContains = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams logs from a Docker container.
    /// </summary>
    /// <param name="containerId">Container ID or name</param>
    /// <param name="tail">Number of lines to fetch initially (default: 500)</param>
    /// <param name="onLogReceived">Callback invoked for each log line with (message, isError)</param>
    /// <param name="dockerContext">Optional docker context targeting a specific engine (external stacks).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StreamContainerLogsAsync(
        string containerId,
        int tail,
        Func<string, bool, Task> onLogReceived,
        string? dockerContext = null,
        CancellationToken cancellationToken = default);
}
