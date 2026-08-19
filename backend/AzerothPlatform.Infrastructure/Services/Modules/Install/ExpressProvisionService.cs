using System.Collections.Concurrent;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

/// <summary>
/// After the first Express build: download the client, disable bots, first-boot, stop, apply the
/// first patch, re-enable bots, then start for real.
/// </summary>
public sealed class ExpressProvisionService : IExpressProvisionService
{
    private static readonly ConcurrentDictionary<string, byte> Running = new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExpressProvisionService> _logger;

    public ExpressProvisionService(
        IServiceScopeFactory scopeFactory,
        ILogger<ExpressProvisionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Enqueue(string stackId)
    {
        if (!Running.TryAdd(stackId, 0))
        {
            return;
        }

        _ = Task.Run(() => RunSafeAsync(stackId));
    }

    private async Task RunSafeAsync(string stackId)
    {
        try
        {
            await RunAsync(stackId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Express provision failed for stack {StackId}", stackId);
            await SetStatusAsync(stackId, ExpressProvisionStatus.Failed, ex.Message, CancellationToken.None);
        }
        finally
        {
            Running.TryRemove(stackId, out _);
        }
    }

    private async Task RunAsync(string stackId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await db.ManagedStacks.SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null || stack.ServerType != ServerType.Express)
        {
            return;
        }

        if (stack.DeploymentTarget != DeploymentTarget.Local)
        {
            await SetStatusAsync(stackId, ExpressProvisionStatus.Failed, "Express Setup is local-only.", cancellationToken);
            return;
        }

        await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Saving Express module choices…", cancellationToken);

        var extras = scope.ServiceProvider.GetRequiredService<IModuleInstallOrchestrator>();
        await extras.SaveChoicesAsync(stackId, new ApplyModuleExtraDataRequest
        {
            IpContentMode = IpContentMode.ServerWideProgression,
        }, cancellationToken);

        var client = scope.ServiceProvider.GetRequiredService<IClientService>();
        var info = await client.GetBaseInfoAsync(stackId, cancellationToken);
        if (!info.Exists && info.DownloadAvailable)
        {
            await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Downloading the base client…", cancellationToken);
            await client.DownloadBaseClientAsync(stackId, cancellationToken);
        }
        else if (!info.Exists)
        {
            _logger.LogWarning("Express stack {StackId} has no base-client URL configured; skipping download.", stackId);
        }

        await InstallSelectedAddonsAsync(scope, stack, cancellationToken);

        await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Disabling playerbots for first boot…", cancellationToken);
        await WritePlayerbotsAsync(scope, stackId, enabled: false, randomBotCount: 0, cancellationToken);

        var stacks = scope.ServiceProvider.GetRequiredService<IStackService>();
        await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Starting the stack for first boot…", cancellationToken);
        await stacks.StartAsync(stackId, cancellationToken);

        var migrations = scope.ServiceProvider.GetRequiredService<IMigrationService>();
        await WaitForDbcBaselineAsync(migrations, stackId, cancellationToken);

        await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Stopping the stack before applying the first patch…", cancellationToken);
        await stacks.StopAsync(stackId, cancellationToken);

        var swp = scope.ServiceProvider.GetRequiredService<IServerWideProgressionService>();
        await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Bootstrapping Server Wide Progression…", cancellationToken);
        await swp.BootstrapAsync(stackId, cancellationToken);

        await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Syncing Server Wide Progression…", cancellationToken);
        var sync = await swp.RunSyncAsync(stackId, cancellationToken);
        if (!sync.Success)
        {
            throw new InvalidOperationException(sync.Error ?? "Server Wide Progression sync failed.");
        }

        await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Applying the first patch…", cancellationToken);
        var overview = await migrations.GetOverviewAsync(stackId, cancellationToken);
        var first = overview.Patches
            .Where(patch => patch.AppliedAt is null)
            .OrderBy(patch => patch.Level)
            .FirstOrDefault();
        if (first is not null)
        {
            var apply = await migrations.ApplyPatchAsync(stackId, first.Key, cancellationToken);
            if (!apply.Success)
            {
                throw new InvalidOperationException(apply.Error ?? $"Failed to apply patch {first.Key}.");
            }
        }

        var botCount = Math.Clamp(stack.RandomBotCount, 0, 2500);
        await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Enabling playerbots…", cancellationToken);
        await WritePlayerbotsAsync(scope, stackId, enabled: true, randomBotCount: botCount, cancellationToken);

        await SetStatusAsync(stackId, ExpressProvisionStatus.Running, "Starting the stack…", cancellationToken);
        await stacks.StartAsync(stackId, cancellationToken);

        await SetStatusAsync(stackId, ExpressProvisionStatus.Completed, "Express Setup finished.", cancellationToken);
    }

    private static async Task WaitForDbcBaselineAsync(
        IMigrationService migrations,
        string stackId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await migrations.TryEnsureServerDbcBaselineAsync(stackId, cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }

        throw new InvalidOperationException("Timed out waiting for the SOAP/DBC baseline after first boot.");
    }

    private async Task InstallSelectedAddonsAsync(
        IServiceScope scope,
        ManagedStackEntity stack,
        CancellationToken cancellationToken)
    {
        List<string> addonIds;
        try
        {
            addonIds = JsonSerializer.Deserialize<List<string>>(stack.AddonIdsJson) ?? [];
        }
        catch (JsonException)
        {
            addonIds = [];
        }

        addonIds = addonIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (addonIds.Count == 0)
        {
            return;
        }

        var addons = scope.ServiceProvider.GetRequiredService<IAddonService>();
        var catalog = addons.GetCatalogDefinitions();
        var byId = catalog.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        addonIds.Sort((left, right) =>
        {
            byId.TryGetValue(left, out var entryA);
            byId.TryGetValue(right, out var entryB);
            if (entryA?.ParentAddonId is { } parentA
                && string.Equals(parentA, right, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (entryB?.ParentAddonId is { } parentB
                && string.Equals(parentB, left, StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }

            return string.Compare(entryA?.Name ?? left, entryB?.Name ?? right, StringComparison.OrdinalIgnoreCase);
        });

        for (var index = 0; index < addonIds.Count; index++)
        {
            var id = addonIds[index];
            var name = byId.TryGetValue(id, out var entry) ? entry.Name : id;
            await SetStatusAsync(
                stack.Id,
                ExpressProvisionStatus.Running,
                $"Installing addon {name} ({index + 1}/{addonIds.Count})…",
                cancellationToken);
            await addons.InstallFromCatalogAsync(stack.Id, id, cancellationToken);
        }
    }

    private static async Task WritePlayerbotsAsync(
        IServiceScope scope,
        string stackId,
        bool enabled,
        int randomBotCount,
        CancellationToken cancellationToken)
    {
        var serverConfig = scope.ServiceProvider.GetRequiredService<IServerConfigService>();
        var files = await serverConfig.ListAsync(stackId, cancellationToken);
        var path = files.Files.FirstOrDefault(file =>
            file.Path.Replace('\\', '/').EndsWith("modules/playerbots.conf", StringComparison.OrdinalIgnoreCase))
            ?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("playerbots.conf is not available yet.");
        }

        var current = await serverConfig.ReadAsync(stackId, path, cancellationToken);
        var content = ServerConfigValueEditor.SetValue(current.Content, "AiPlayerbot.Enabled", enabled ? "1" : "0");
        content = ServerConfigValueEditor.SetValue(content, "AiPlayerbot.RandomBotAutologin", randomBotCount > 0 ? "1" : "0");
        content = ServerConfigValueEditor.SetValue(content, "AiPlayerbot.MinRandomBots", randomBotCount.ToString());
        content = ServerConfigValueEditor.SetValue(content, "AiPlayerbot.MaxRandomBots", randomBotCount.ToString());
        await serverConfig.SaveAsync(stackId, path, content, cancellationToken);
    }

    private async Task SetStatusAsync(
        string stackId,
        ExpressProvisionStatus status,
        string message,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await db.ManagedStacks.SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return;
        }

        stack.ExpressProvisionStatus = status;
        stack.ExpressProvisionMessage = message;
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Express provision {StackId}: {Status} — {Message}", stackId, status, message);
    }
}
