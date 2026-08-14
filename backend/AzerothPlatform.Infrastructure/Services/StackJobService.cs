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

    public StackJobStatusDto? GetStatus(string stackId)
    {
        if (!Jobs.TryGetValue(stackId, out var status))
        {
            return null;
        }

        // Apply-public-host is a one-shot UI progress job — do not reattach after it finishes.
        if (status.Action == StackJobAction.ApplyPublicHost
            && status.Phase is StackJobPhase.Completed or StackJobPhase.Failed)
        {
            Jobs.TryRemove(stackId, out _);
            return null;
        }

        return status;
    }

    public StackJobStatusDto Enqueue(
        string stackId,
        StackJobAction action,
        PublicHostApplyPlanDto? publicHostPlan = null,
        bool supersedeRunning = false)
    {
        // Serialize per stack: if a job is still running, return it rather than racing docker (this is
        // what prevents a second "Start" click from launching a duplicate operation).
        if (!supersedeRunning
            && Jobs.TryGetValue(stackId, out var existing)
            && existing.IsRunning)
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
            StartedAt = DateTime.UtcNow,
            Steps = action == StackJobAction.ApplyPublicHost
                ? CreatePublicHostSteps(publicHostPlan ?? new PublicHostApplyPlanDto())
                : [],
        };
        Jobs[stackId] = status;
        Publish(status);

        // Detached from the request: the operation must run to completion even if the caller disconnects.
        _ = Task.Run(() => RunAsync(stackId, action), CancellationToken.None);
        return status;
    }

    public void ReportProgress(string stackId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)
            || !Jobs.TryGetValue(stackId, out var status)
            || !status.IsRunning)
        {
            return;
        }

        status.Message = message;
        Publish(status);
    }

    internal void ReportPublicHostStep(string stackId, PublicHostApplyStepDto step)
    {
        if (!Jobs.TryGetValue(stackId, out var status))
        {
            return;
        }

        var existing = status.Steps.FirstOrDefault(s => s.Id == step.Id);
        if (existing is null)
        {
            status.Steps.Add(step);
        }
        else
        {
            existing.Status = step.Status;
            existing.Detail = step.Detail;
        }

        status.Message = step.Status switch
        {
            PublicHostApplyStepStatus.Failed => step.Detail ?? step.Label,
            PublicHostApplyStepStatus.Running => step.Label,
            _ => status.Message,
        };
        Publish(status);
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
                StackJobAction.Stop => await stacks.ForceStopAsync(stackId),
                StackJobAction.Restart => await stacks.RestartAsync(stackId),
                StackJobAction.ApplyPublicHost => await RunApplyPublicHostAsync(stackId, stacks),
                _ => false
            };

            if (action == StackJobAction.ApplyPublicHost)
            {
                return;
            }

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

    private async Task<bool> RunApplyPublicHostAsync(string stackId, IStackService stacks)
    {
        try
        {
            await stacks.ApplyStackPublicHostLiveAsync(
                stackId,
                step => ReportPublicHostStep(stackId, step),
                CancellationToken.None);
            Complete(stackId, success: true, message: CompletedMessage(StackJobAction.ApplyPublicHost), error: null);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply public host job failed for stack {StackId}", stackId);
            Complete(stackId, success: false, message: FailedMessage(StackJobAction.ApplyPublicHost), error: ex.Message);
            return false;
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

        if (status.Action == StackJobAction.ApplyPublicHost)
        {
            Jobs.TryRemove(stackId, out _);
        }
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
        StackJobAction.ApplyPublicHost => StackJobPhase.ApplyingPublicHost,
        _ => StackJobPhase.Starting
    };

    private static List<PublicHostApplyStepDto> CreatePublicHostSteps(PublicHostApplyPlanDto plan)
    {
        var steps = new List<PublicHostApplyStepDto>();

        if (!plan.DatabaseRunning)
        {
            steps.Add(new() { Id = "database", Label = "Start database" });
        }

        steps.Add(new() { Id = "realmlist", Label = "Update realmlist in MySQL" });

        if (plan.ClientEnabled)
        {
            if (!plan.ClientRunning)
            {
                steps.Add(new() { Id = "client", Label = "Start client server" });
            }

            steps.Add(new() { Id = "registry", Label = "Update launcher registry" });
            steps.Add(new() { Id = "rescan", Label = "Refresh client manifest" });
        }

        if (plan.WasFullyStopped)
        {
            steps.Add(new() { Id = "recreate-auth", Label = "Recreate auth server" });
            steps.Add(new() { Id = "recreate-world", Label = "Recreate world server" });
            if (plan.ArmoryEnabled)
            {
                steps.Add(new() { Id = "recreate-armory", Label = "Recreate armory" });
                steps.Add(new() { Id = "recreate-armory-assets", Label = "Recreate armory assets" });
            }

            if (plan.ClientEnabled)
            {
                steps.Add(new() { Id = "recreate-client", Label = "Recreate client server" });
            }

            steps.Add(new() { Id = "restore", Label = "Restore previous stack state" });
        }
        else
        {
            if (plan.AuthRunning)
            {
                steps.Add(new() { Id = "recreate-auth", Label = "Recreate auth server" });
            }

            if (plan.WorldRunning)
            {
                steps.Add(new() { Id = "recreate-world", Label = "Recreate world server" });
            }

            if (plan.ArmoryEnabled && plan.ArmoryRunning)
            {
                steps.Add(new() { Id = "recreate-armory", Label = "Recreate armory" });
            }

            if (plan.ArmoryEnabled && plan.ArmoryAssetsRunning)
            {
                steps.Add(new() { Id = "recreate-armory-assets", Label = "Recreate armory assets" });
            }

            if (plan.ClientEnabled && plan.ClientRunning)
            {
                steps.Add(new() { Id = "recreate-client", Label = "Recreate client server" });
            }
        }

        return steps;
    }

    private static string InProgressMessage(StackJobAction action) => action switch
    {
        StackJobAction.Start => "Starting the stack (ensuring images, seeding volumes, starting containers)…",
        StackJobAction.StartDatabase => "Starting the database container…",
        StackJobAction.Stop => "Stopping the stack…",
        StackJobAction.Restart => "Restarting the stack…",
        StackJobAction.ApplyPublicHost => "Applying stack IP address…",
        _ => "Working…"
    };

    private static string CompletedMessage(StackJobAction action) => action switch
    {
        StackJobAction.Start => "Stack started.",
        StackJobAction.StartDatabase => "Database started.",
        StackJobAction.Stop => "Stack stopped.",
        StackJobAction.Restart => "Stack restarted.",
        StackJobAction.ApplyPublicHost => "Stack IP address applied.",
        _ => "Done."
    };

    private static string FailedMessage(StackJobAction action) => action switch
    {
        StackJobAction.Start => "Failed to start the stack.",
        StackJobAction.StartDatabase => "Failed to start the database.",
        StackJobAction.Stop => "Failed to stop the stack.",
        StackJobAction.Restart => "Failed to restart the stack.",
        StackJobAction.ApplyPublicHost => "Failed to apply the stack IP address.",
        _ => "Operation failed."
    };
}
