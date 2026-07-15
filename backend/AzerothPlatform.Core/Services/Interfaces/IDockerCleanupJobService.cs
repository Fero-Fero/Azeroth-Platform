using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Runs Docker disk reclaim (prune cache, dangling layers, unused images) as a detached background job
/// so the operation survives the HTTP request and is not cancelled when the user navigates away.
/// </summary>
public interface IDockerCleanupJobService
{
    /// <summary>Current reclaim job, or null when none has ever run.</summary>
    DockerCleanupJobStatusDto? GetStatus();

    /// <summary>
    /// Starts a detached cleanup job. If one is already running, returns the in-flight job unchanged.
    /// </summary>
    DockerCleanupJobStatusDto Enqueue(DockerCleanupJobAction action = DockerCleanupJobAction.ReclaimDiskSpace);
}

/// <summary>Publishes Docker reclaim job status to connected clients (SignalR).</summary>
public interface IDockerCleanupEventPublisher
{
    Task PublishStatusAsync(DockerCleanupJobStatusDto status);
}

/// <summary>SignalR group name for Docker disk-reclaim job updates.</summary>
public static class DockerCleanupJobGroups
{
    public const string SignalR = "docker-cleanup";
}
