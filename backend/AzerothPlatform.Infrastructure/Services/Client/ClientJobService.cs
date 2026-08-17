using System.Collections.Concurrent;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Runs client file-server start/stop/restart/recreate as detached background jobs. Mirrors
/// <see cref="ArmoryJobService"/>.
/// </summary>
public sealed class ClientJobService : IClientJobService
{
    private static readonly ConcurrentDictionary<string, ClientJobStatusDto> Jobs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClientEventPublisher _publisher;
    private readonly ILogger<ClientJobService> _logger;

    public ClientJobService(
        IServiceScopeFactory scopeFactory,
        IClientEventPublisher publisher,
        ILogger<ClientJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _logger = logger;
    }

    public ClientJobStatusDto? GetStatus(string stackId) =>
        Jobs.TryGetValue(stackId, out var status) ? status : null;

    public ClientJobStatusDto Enqueue(string stackId, ClientJobAction action)
    {
        if (Jobs.TryGetValue(stackId, out var existing) && existing.IsRunning)
        {
            return existing;
        }

        var status = new ClientJobStatusDto
        {
            StackId = stackId,
            JobId = Guid.NewGuid().ToString("N"),
            Action = action,
            Phase = InProgressPhase(action),
            Message = InProgressMessage(action),
            StartedAt = DateTime.UtcNow,
        };
        Jobs[stackId] = status;
        Publish(status);

        _ = Task.Run(() => RunAsync(stackId, action), CancellationToken.None);
        return status;
    }

    private async Task RunAsync(string stackId, ClientJobAction action)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var stacks = scope.ServiceProvider.GetRequiredService<IStackService>();

            var ok = action switch
            {
                ClientJobAction.Start => await stacks.StartClientAsync(stackId),
                ClientJobAction.Stop => await stacks.StopClientAsync(stackId),
                ClientJobAction.Restart => await stacks.RestartClientAsync(stackId),
                ClientJobAction.Recreate => await stacks.StartClientAsync(stackId, forceRecreate: true),
                _ => false,
            };

            if (ok)
            {
                Complete(stackId, success: true, message: CompletedMessage(action), error: null);
            }
            else
            {
                Complete(
                    stackId,
                    success: false,
                    message: "Client operation did not complete.",
                    error: "Stack not found or the operation returned no result.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client {Action} job failed for stack {StackId}", action, stackId);
            Complete(stackId, success: false, message: "Client operation failed.", error: ex.Message);
        }
    }

    private void Complete(string stackId, bool success, string message, string? error)
    {
        if (!Jobs.TryGetValue(stackId, out var status))
        {
            return;
        }

        status.Phase = success ? ClientJobPhase.Completed : ClientJobPhase.Failed;
        status.Message = message;
        status.Error = error;
        status.Success = success;
        status.FinishedAt = DateTime.UtcNow;
        Publish(status);
    }

    private void Publish(ClientJobStatusDto status) => _ = _publisher.PublishStatusAsync(status);

    private static ClientJobPhase InProgressPhase(ClientJobAction action) => action switch
    {
        ClientJobAction.Start => ClientJobPhase.Starting,
        ClientJobAction.Stop => ClientJobPhase.Stopping,
        ClientJobAction.Restart => ClientJobPhase.Restarting,
        ClientJobAction.Recreate => ClientJobPhase.Recreating,
        _ => ClientJobPhase.Starting,
    };

    private static string InProgressMessage(ClientJobAction action) => action switch
    {
        ClientJobAction.Start => "Building (if needed) and starting the client file server…",
        ClientJobAction.Stop => "Stopping the client file server…",
        ClientJobAction.Restart => "Restarting the client file server…",
        ClientJobAction.Recreate => "Rebuilding the client image and restarting…",
        _ => "Working…",
    };

    private static string CompletedMessage(ClientJobAction action) => action switch
    {
        ClientJobAction.Start => "Client file server started.",
        ClientJobAction.Stop => "Client file server stopped.",
        ClientJobAction.Restart => "Client file server restarted.",
        ClientJobAction.Recreate => "Client rebuilt and restarted.",
        _ => "Done.",
    };
}
