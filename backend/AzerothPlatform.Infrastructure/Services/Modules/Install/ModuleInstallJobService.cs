using System.Collections.Concurrent;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

public sealed class ModuleInstallJobService : IModuleInstallJobService
{
    private const int MaxLogLines = 500;
    private static readonly ConcurrentDictionary<string, ModuleInstallJobStatusDto> Jobs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ModuleInstallJobService> _logger;

    public ModuleInstallJobService(
        IServiceScopeFactory scopeFactory,
        ILogger<ModuleInstallJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public ModuleInstallJobStatusDto? GetStatus(string stackId) =>
        Jobs.TryGetValue(stackId, out var status) ? status : null;

    public ModuleInstallJobStatusDto EnqueuePrepare(string stackId, ApplyModuleExtraDataRequest request)
    {
        return Start(stackId, "Preparing module extra data…", async orchestrator =>
        {
            await orchestrator.PrepareAsync(stackId, request, line => AddLog(stackId, line), CancellationToken.None);
        }, "Module extras prepared.", "Module extra-data prepare failed");
    }

    public ModuleInstallJobStatusDto EnqueueDeposit(string stackId)
    {
        return Start(stackId, "Setup module content…", async orchestrator =>
        {
            await orchestrator.DepositAsync(stackId, line => AddLog(stackId, line), CancellationToken.None);
        }, "Module content applied.", "Setup module content failed");
    }

    public ModuleInstallJobStatusDto Enqueue(string stackId, ApplyModuleExtraDataRequest request)
    {
        return Start(stackId, "Applying module extra data…", async orchestrator =>
        {
            await orchestrator.ApplyAsync(stackId, request, line => AddLog(stackId, line), CancellationToken.None);
        }, "Module extra data applied.", "Module extra-data apply failed");
    }

    private ModuleInstallJobStatusDto Start(
        string stackId,
        string runningMessage,
        Func<IModuleInstallOrchestrator, Task> run,
        string successMessage,
        string failLog)
    {
        if (Jobs.TryGetValue(stackId, out var existing) && existing.IsRunning)
        {
            return existing;
        }

        var status = new ModuleInstallJobStatusDto
        {
            StackId = stackId,
            JobId = Guid.NewGuid().ToString("N"),
            Phase = ModuleInstallJobPhase.Running,
            Message = runningMessage,
            StartedAt = DateTime.UtcNow
        };
        Jobs[stackId] = status;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IModuleInstallOrchestrator>();
                await run(orchestrator);
                Complete(stackId, success: true, successMessage, error: null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{FailLog} for stack {StackId}", failLog, stackId);
                Complete(stackId, success: false, $"{failLog}.", ex.Message);
            }
        });
        return status;
    }

    private void AddLog(string stackId, string line)
    {
        if (!Jobs.TryGetValue(stackId, out var status))
        {
            return;
        }

        var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {line}";
        var next = new List<string>(status.RecentLogs) { stamped };
        if (next.Count > MaxLogLines)
        {
            next.RemoveRange(0, next.Count - MaxLogLines);
        }

        status.RecentLogs = next;
        status.Message = line;
    }

    private static void Complete(string stackId, bool success, string message, string? error)
    {
        if (!Jobs.TryGetValue(stackId, out var status))
        {
            return;
        }

        status.Phase = success ? ModuleInstallJobPhase.Completed : ModuleInstallJobPhase.Failed;
        status.Success = success;
        status.Message = message;
        status.Error = error;
        status.FinishedAt = DateTime.UtcNow;
    }
}
