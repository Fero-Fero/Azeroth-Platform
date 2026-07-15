using System.Collections.Concurrent;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Runs stack start/stop/restart/start-database as detached background jobs. State is held in a static
/// map keyed by stack id (this service is a singleton) so status survives the request that started the
/// job and can be reattached to after navigating away or refreshing, via <see cref="GetStatus"/> or the
/// SignalR stream. Mirrors <see cref="ArmoryJobService"/>.
/// </summary>
public sealed class StackJobService : IStackJobService
{
    private static readonly ConcurrentDictionary<string, StackJobStatusDto> Jobs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStackEventPublisher _publisher;
    private readonly ILogger<StackJobService> _logger;

    public StackJobService(
        IServiceScopeFactory scopeFactory,
        IStackEventPublisher publisher,
        ILogger<StackJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _logger = logger;
    }

    public StackJobStatusDto? GetStatus(string stackId) =>
        Jobs.TryGetValue(stackId, out var status) ? status : null;

    public StackJobStatusDto Enqueue(string stackId, StackJobAction action)
    {
        // Serialize per stack: if a job is still running, return it rather than racing docker (this is
        // what prevents a second "Start" click from launching a duplicate operation).
        if (Jobs.TryGetValue(stackId, out var existing) && existing.IsRunning)
        {
            return existing;
        }

        var status = new StackJobStatusDto
        {
            StackId = stackId,
            JobId = Guid.NewGuid().ToString("N"),
            Action = action,
            Phase = InProgressPhase(action),
            Message = InProgressMessage(action),
            StartedAt = DateTime.UtcNow
        };
        Jobs[stackId] = status;
        Publish(status);

        // Detached from the request: the operation must run to completion even if the caller disconnects.
        _ = Task.Run(() => RunAsync(stackId, action), CancellationToken.None);
        return status;
    }

    private async Task RunAsync(string stackId, StackJobAction action)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var stacks = scope.ServiceProvider.GetRequiredService<IStackService>();

            var ok = action switch
            {
                StackJobAction.Start => await stacks.StartAsync(stackId),
                StackJobAction.StartDatabase => await stacks.StartDatabaseAsync(stackId),
                StackJobAction.Stop => await stacks.StopAsync(stackId),
                StackJobAction.Restart => await stacks.RestartAsync(stackId),
                _ => false
            };

            if (ok)
            {
                Complete(stackId, success: true, message: CompletedMessage(action), error: null);
            }
            else
            {
                Complete(stackId, success: false, message: "Operation did not complete.",
                    error: "Stack not found or the operation returned no result.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stack {Action} job failed for stack {StackId}", action, stackId);
            Complete(stackId, success: false, message: FailedMessage(action), error: ex.Message);
        }
    }

    private void Complete(string stackId, bool success, string message, string? error)
    {
        if (!Jobs.TryGetValue(stackId, out var status))
        {
            return;
        }

        status.Phase = success ? StackJobPhase.Completed : StackJobPhase.Failed;
        status.Message = message;
        status.Error = error;
        status.Success = success;
        status.FinishedAt = DateTime.UtcNow;
        Publish(status);
    }

    private void Publish(StackJobStatusDto status)
    {
        // Fire-and-forget: a slow/failed SignalR push must not affect the job itself.
        _ = _publisher.PublishStatusAsync(status);
    }

    private static StackJobPhase InProgressPhase(StackJobAction action) => action switch
    {
        StackJobAction.Start => StackJobPhase.Starting,
        StackJobAction.StartDatabase => StackJobPhase.StartingDatabase,
        StackJobAction.Stop => StackJobPhase.Stopping,
        StackJobAction.Restart => StackJobPhase.Restarting,
        _ => StackJobPhase.Starting
    };

    private static string InProgressMessage(StackJobAction action) => action switch
    {
        StackJobAction.Start => "Starting the stack (ensuring images, seeding volumes, starting containers)…",
        StackJobAction.StartDatabase => "Starting the database container…",
        StackJobAction.Stop => "Stopping the stack…",
        StackJobAction.Restart => "Restarting the stack…",
        _ => "Working…"
    };

    private static string CompletedMessage(StackJobAction action) => action switch
    {
        StackJobAction.Start => "Stack started.",
        StackJobAction.StartDatabase => "Database started.",
        StackJobAction.Stop => "Stack stopped.",
        StackJobAction.Restart => "Stack restarted.",
        _ => "Done."
    };

    private static string FailedMessage(StackJobAction action) => action switch
    {
        StackJobAction.Start => "Failed to start the stack.",
        StackJobAction.StartDatabase => "Failed to start the database.",
        StackJobAction.Stop => "Failed to stop the stack.",
        StackJobAction.Restart => "Failed to restart the stack.",
        _ => "Operation failed."
    };
}
