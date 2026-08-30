using System.Collections.Concurrent;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Runs armory start/stop/restart/rebuild as detached background jobs. State is held in a static map
/// keyed by stack id (this service is a singleton) so status survives the request that started the job
/// and can be reattached to after a page refresh via <see cref="GetStatus"/> or the SignalR stream.
/// </summary>
public sealed class ArmoryJobService : IArmoryJobService
{
    private const string ArmoryService = "frontend-armory";

    private static readonly ConcurrentDictionary<string, ArmoryJobStatusDto> Jobs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IArmoryEventPublisher _publisher;
    private readonly ILogger<ArmoryJobService> _logger;

    public ArmoryJobService(
        IServiceScopeFactory scopeFactory,
        IArmoryEventPublisher publisher,
        ILogger<ArmoryJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _logger = logger;
    }

    public ArmoryJobStatusDto? GetStatus(string stackId) =>
        Jobs.TryGetValue(stackId, out var status) ? status : null;

    public ArmoryJobStatusDto Enqueue(string stackId, ArmoryJobAction action)
    {
        // Serialize per stack: if a job is still running, return it rather than racing docker.
        if (Jobs.TryGetValue(stackId, out var existing) && existing.IsRunning)
        {
            return existing;
        }

        var status = new ArmoryJobStatusDto
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

    private async Task RunAsync(string stackId, ArmoryJobAction action)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var stacks = scope.ServiceProvider.GetRequiredService<IStackService>();

            // The DBC sync converts server DBCs into the armory dataset, then rebuilds & recreates the
            // armory (same as Rebuild) so the freshly baked CSVs are actually loaded.
            if (action == ArmoryJobAction.SyncDbc)
            {
                var dbc = scope.ServiceProvider.GetRequiredService<IArmoryDbcService>();
                var sync = await dbc.SyncFromServerAsync(stackId, line => AddLog(stackId, line));
                var summary = sync.Failed.Count > 0
                    ? $"Converted {sync.Exported.Count} DBC table(s), {sync.Failed.Count} failed. Rebuilding the armory image…"
                    : $"Converted {sync.Exported.Count} DBC table(s). Rebuilding the armory image…";
                UpdateMessage(stackId, summary);
                AddLog(stackId, summary);
            }

            // Rebuild and DBC sync both bake a fresh armory image and recreate the container (slow step).
            if (action is ArmoryJobAction.Rebuild or ArmoryJobAction.SyncDbc)
            {
                AddLog(stackId, "Building the armory image and recreating the container (this can take a few minutes)…");
            }

            var ok = action switch
            {
                ArmoryJobAction.Start => await stacks.StartArmoryAsync(stackId),
                ArmoryJobAction.Stop => await stacks.StopArmoryAsync(stackId),
                ArmoryJobAction.Restart => await stacks.ServiceActionAsync(stackId, ArmoryService, StackServiceAction.Restart),
                ArmoryJobAction.Rebuild or ArmoryJobAction.SyncDbc =>
                    await stacks.ServiceActionAsync(stackId, ArmoryService, StackServiceAction.Recreate),
                _ => false
            };

            if (ok)
            {
                // The "static rebuild pending" flag is cleared where the assets are actually baked
                // (ArmoryImageService.BuildImageAsync), so every image-rebuild path - Rebuild here, the
                // armory "Rebuild & Restart" service action, and a DBC sync - reconciles the UI prompt.
                Complete(stackId, success: true, message: CompletedMessage(action), error: null);
            }
            else
            {
                Complete(stackId, success: false, message: "Armory operation did not complete.",
                    error: "Stack not found or the operation returned no result.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Armory {Action} job failed for stack {StackId}", action, stackId);
            Complete(stackId, success: false, message: "Armory operation failed.", error: ex.Message);
        }
    }

    private void UpdateMessage(string stackId, string message)
    {
        if (!Jobs.TryGetValue(stackId, out var status))
        {
            return;
        }

        status.Message = message;
        Publish(status);
    }

    // Keep the running log bounded; high-level step lines only (not raw docker build output).
    private const int MaxLogLines = 500;

    private void AddLog(string stackId, string line)
    {
        if (!Jobs.TryGetValue(stackId, out var status))
        {
            return;
        }

        var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {line}";
        // Copy-on-write: swap in a new list so an in-flight SignalR serialization keeps its own snapshot
        // (the job runs on a background task while SignalR serializes asynchronously).
        var next = new List<string>(status.RecentLogs) { stamped };
        if (next.Count > MaxLogLines)
        {
            next.RemoveRange(0, next.Count - MaxLogLines);
        }

        status.RecentLogs = next;
        Publish(status);
    }

    private void Complete(string stackId, bool success, string message, string? error)
    {
        if (!Jobs.TryGetValue(stackId, out var status))
        {
            return;
        }

        status.Phase = success ? ArmoryJobPhase.Completed : ArmoryJobPhase.Failed;
        status.Message = message;
        status.Error = error;
        status.Success = success;
        status.FinishedAt = DateTime.UtcNow;

        // Close out the log with a terminal line so the panel shows a clear result (only for jobs that
        // actually produced a log, i.e. rebuild/DBC sync).
        if (status.RecentLogs.Count > 0)
        {
            var terminal = success ? message : (error is null ? message : $"{message} {error}");
            var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {terminal}";
            status.RecentLogs = new List<string>(status.RecentLogs) { stamped };
        }

        Publish(status);
    }

    private void Publish(ArmoryJobStatusDto status)
    {
        // Fire-and-forget: a slow/failed SignalR push must not affect the job itself.
        _ = _publisher.PublishStatusAsync(status);
    }

    private static ArmoryJobPhase InProgressPhase(ArmoryJobAction action) => action switch
    {
        ArmoryJobAction.Start => ArmoryJobPhase.Starting,
        ArmoryJobAction.Stop => ArmoryJobPhase.Stopping,
        ArmoryJobAction.Restart => ArmoryJobPhase.Restarting,
        ArmoryJobAction.Rebuild => ArmoryJobPhase.Rebuilding,
        ArmoryJobAction.SyncDbc => ArmoryJobPhase.SyncingDbc,
        _ => ArmoryJobPhase.Starting
    };

    private static string InProgressMessage(ArmoryJobAction action) => action switch
    {
        ArmoryJobAction.Start => "Building (if needed) and starting the armory…",
        ArmoryJobAction.Stop => "Stopping the armory…",
        ArmoryJobAction.Restart => "Restarting the armory…",
        ArmoryJobAction.Rebuild => "Rebuilding the armory image and restarting…",
        ArmoryJobAction.SyncDbc => "Extracting DBCs from the server for the armory…",
        _ => "Working…"
    };

    private static string CompletedMessage(ArmoryJobAction action) => action switch
    {
        ArmoryJobAction.Start => "Armory started.",
        ArmoryJobAction.Stop => "Armory stopped.",
        ArmoryJobAction.Restart => "Armory restarted.",
        ArmoryJobAction.Rebuild => "Armory rebuilt and restarted.",
        ArmoryJobAction.SyncDbc => "Server DBCs synced to the armory and reloaded.",
        _ => "Done."
    };
}
