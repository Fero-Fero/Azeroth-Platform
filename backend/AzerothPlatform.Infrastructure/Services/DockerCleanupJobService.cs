using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Global background worker for Docker cleanup. Only one job runs at a time; state is held in memory so the
/// UI can reattach via GET status or SignalR after navigating away.
/// </summary>
public sealed class DockerCleanupJobService : IDockerCleanupJobService
{
    private static DockerCleanupJobStatusDto? Job;
    private static readonly object Gate = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDockerCleanupEventPublisher _publisher;
    private readonly ILogger<DockerCleanupJobService> _logger;

    public DockerCleanupJobService(
        IServiceScopeFactory scopeFactory,
        IDockerCleanupEventPublisher publisher,
        ILogger<DockerCleanupJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _logger = logger;
    }

    public DockerCleanupJobStatusDto? GetStatus()
    {
        lock (Gate)
        {
            return Job;
        }
    }

    public DockerCleanupJobStatusDto Enqueue(DockerCleanupJobAction action = DockerCleanupJobAction.ReclaimDiskSpace)
    {
        lock (Gate)
        {
            if (Job is { IsRunning: true })
            {
                return Job;
            }

            Job = new DockerCleanupJobStatusDto
            {
                JobId = Guid.NewGuid().ToString("N"),
                Action = action,
                Phase = DockerCleanupJobPhase.Running,
                Message = InProgressMessage(action),
                StartedAt = DateTime.UtcNow,
            };
        }

        Publish(Job!);

        _ = Task.Run(() => RunAsync(action), CancellationToken.None);
        return Job!;
    }

    private async Task RunAsync(DockerCleanupJobAction action)
    {
        DockerCleanupJobStatusDto status;
        lock (Gate)
        {
            status = Job ?? throw new InvalidOperationException("Docker cleanup job was not initialized.");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var docker = scope.ServiceProvider.GetRequiredService<IStackDockerService>();

            var before = await docker.GetDiskUsageAsync(CancellationToken.None);
            lock (Gate)
            {
                if (Job is null)
                {
                    return;
                }

                Job.EstimatedReclaimableBytes = EstimateReclaimableBytes(action, before);
            }

            Publish(status);

            var result = action switch
            {
                DockerCleanupJobAction.CleanupOldBuilds => await docker.CleanupOldBuildsAsync(CancellationToken.None),
                _ => await docker.CleanupUnusedAsync(CancellationToken.None),
            };
            Complete(success: result.Success, message: result.Message, error: null, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docker cleanup job failed ({Action})", action);
            Complete(success: false, message: FailedMessage(action), error: ex.Message, result: null);
        }
    }

    private static long EstimateReclaimableBytes(DockerCleanupJobAction action, DockerDiskUsageDto usage) =>
        action switch
        {
            DockerCleanupJobAction.CleanupOldBuilds =>
                usage.DockerImagesReclaimableBytes,
            _ => usage.ReclaimableBytes,
        };

    private void Complete(bool success, string message, string? error, DockerCleanupResultDto? result)
    {
        lock (Gate)
        {
            if (Job is null)
            {
                return;
            }

            Job.Phase = success ? DockerCleanupJobPhase.Completed : DockerCleanupJobPhase.Failed;
            Job.Message = message;
            Job.Error = error;
            Job.Success = success;
            Job.FinishedAt = DateTime.UtcNow;
            if (result is not null)
            {
                Job.FreedBytes = result.FreedBytes;
                Job.RemovedImages = result.RemovedImages;
                Job.RemovedBuildDirs = result.RemovedBuildDirs;
            }
        }

        Publish(Job!);
    }

    private void Publish(DockerCleanupJobStatusDto status)
    {
        _ = _publisher.PublishStatusAsync(status);
    }

    private static string InProgressMessage(DockerCleanupJobAction action) => action switch
    {
        DockerCleanupJobAction.CleanupOldBuilds =>
            "Cleaning up old builds (dangling layers, unused stack images, orphaned checkouts)…",
        _ => "Reclaiming disk space (pruning build cache, dangling layers, and unused images)…",
    };

    private static string FailedMessage(DockerCleanupJobAction action) => action switch
    {
        DockerCleanupJobAction.CleanupOldBuilds => "Failed to clean up old builds.",
        _ => "Failed to reclaim disk space.",
    };
}
