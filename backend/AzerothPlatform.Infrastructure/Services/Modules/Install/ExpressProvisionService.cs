using System.Collections.Concurrent;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

/// <summary>
/// Express Setup pipeline with checkpoints. SOAP manager account and game account admin/admin stay distinct.
/// </summary>
public sealed class ExpressProvisionService : IExpressProvisionService
{
    public const string GameAccountUsername = "admin";
    public const string GameAccountPassword = "admin";
    public const string AhBotGuidKey = "AC_AUCTION_HOUSE_BOT_GUIDS";
    public const string DungeonClearModuleId = "mod-dungeon-clear";
    public const string DungeonClearAddonId = "dungeon-clear-addon";

    private static readonly ExpressProvisionPhase[] Pipeline =
    [
        ExpressProvisionPhase.SaveChoices,
        ExpressProvisionPhase.DisableBots,
        ExpressProvisionPhase.StartStack,
        ExpressProvisionPhase.SoapDbc,
        ExpressProvisionPhase.AhBot,
        ExpressProvisionPhase.GameAccount,
        ExpressProvisionPhase.StopStack,
        ExpressProvisionPhase.WaitClient,
        ExpressProvisionPhase.SwpSync,
        ExpressProvisionPhase.EnableBots,
        ExpressProvisionPhase.Launcher,
        ExpressProvisionPhase.Addons,
    ];

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

    public void Start(string stackId)
    {
        using var scope = _scopeFactory.CreateScope();
        var stack = RequireExpressStack(scope, stackId);
        if (stack.ExpressProvisionStatus != ExpressProvisionStatus.Pending)
        {
            throw new InvalidOperationException("Express Setup is not waiting to start.");
        }

        PersistRunning(stackId, ExpressProvisionPhase.SaveChoices, "Starting Express Setup…");
        Enqueue(stackId);
    }

    public void ContinueAfterClient(string stackId)
    {
        using var scope = _scopeFactory.CreateScope();
        var stack = RequireExpressStack(scope, stackId);
        if (stack.ExpressProvisionStatus != ExpressProvisionStatus.WaitingForClient)
        {
            throw new InvalidOperationException("Express Setup is not waiting for a client.");
        }

        // Client presence is re-checked in the WaitClient pipeline phase. Inspecting the volume
        // here blocked the HTTP response for seconds and made Continue feel stuck after a refresh.
        PersistRunning(stackId, ExpressProvisionPhase.WaitClient, "Starting the client files service…");
        Enqueue(stackId);
    }

    public void Retry(string stackId)
    {
        using var scope = _scopeFactory.CreateScope();
        var stack = RequireExpressStack(scope, stackId);
        if (stack.ExpressProvisionStatus != ExpressProvisionStatus.Failed)
        {
            throw new InvalidOperationException("Express Setup can only be retried after a failure.");
        }

        var phase = stack.ExpressProvisionPhase;
        if (phase is ExpressProvisionPhase.None or ExpressProvisionPhase.Done)
        {
            phase = ExpressProvisionPhase.SaveChoices;
        }

        PersistRunning(stackId, phase, "Retrying Express Setup…");
        Enqueue(stackId);
    }

    public void DismissReadyNotice(string stackId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = db.ManagedStacks.SingleOrDefault(item => item.Id == stackId);
        if (stack is null || stack.ServerType != ServerType.Express)
        {
            throw new InvalidOperationException("Express Setup is not available for this stack.");
        }

        stack.ExpressReadyNoticePending = false;
        db.SaveChanges();
    }

    public void ResumeInterrupted()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var running = db.ManagedStacks
            .AsNoTracking()
            .Where(item =>
                item.ServerType == ServerType.Express
                && item.ExpressProvisionStatus == ExpressProvisionStatus.Running)
            .Select(item => item.Id)
            .ToList();

        foreach (var stackId in running)
        {
            _logger.LogInformation("Resuming Express Setup for stack {StackId} after manager start.", stackId);
            Enqueue(stackId);
        }
    }

    private static ManagedStackEntity RequireExpressStack(IServiceScope scope, string stackId)
    {
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = db.ManagedStacks.AsNoTracking().SingleOrDefault(item => item.Id == stackId);
        if (stack is null || stack.ServerType != ServerType.Express)
        {
            throw new InvalidOperationException("Express Setup is not available for this stack.");
        }

        return stack;
    }

    private void Enqueue(string stackId)
    {
        if (!Running.TryAdd(stackId, 0))
        {
            return;
        }

        _ = Task.Run(() => RunSafeAsync(stackId));
    }

    private void PersistRunning(string stackId, ExpressProvisionPhase phase, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = db.ManagedStacks.SingleOrDefault(item => item.Id == stackId);
        if (stack is null)
        {
            return;
        }

        stack.ExpressProvisionStatus = ExpressProvisionStatus.Running;
        stack.ExpressProvisionPhase = phase;
        stack.ExpressProvisionMessage = message;
        db.SaveChanges();
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
            await CheckpointAsync(
                stackId,
                ExpressProvisionStatus.Failed,
                phase: null,
                ex.Message,
                CancellationToken.None);
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
            await CheckpointAsync(
                stackId,
                ExpressProvisionStatus.Failed,
                ExpressProvisionPhase.SaveChoices,
                "Express Setup is local-only.",
                cancellationToken);
            return;
        }

        if (stack.ExpressProvisionStatus == ExpressProvisionStatus.Completed)
        {
            return;
        }

        var start = stack.ExpressProvisionPhase;
        if (start is ExpressProvisionPhase.None or ExpressProvisionPhase.Done)
        {
            start = ExpressProvisionPhase.SaveChoices;
        }

        await RunFromPhaseAsync(scope, stack, start, cancellationToken);
    }

    private async Task RunFromPhaseAsync(
        IServiceScope scope,
        ManagedStackEntity stack,
        ExpressProvisionPhase start,
        CancellationToken cancellationToken)
    {
        var startIndex = Array.IndexOf(Pipeline, start);
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        for (var index = startIndex; index < Pipeline.Length; index++)
        {
            var phase = Pipeline[index];
            if (phase == ExpressProvisionPhase.WaitClient)
            {
                var stacks = scope.ServiceProvider.GetRequiredService<IStackService>();
                await CheckpointAsync(
                    stack.Id,
                    ExpressProvisionStatus.Running,
                    ExpressProvisionPhase.WaitClient,
                    "Starting the client files service…",
                    cancellationToken);
                await stacks.StartClientAsync(stack.Id, forceRecreate: false, cancellationToken);

                var client = scope.ServiceProvider.GetRequiredService<IClientService>();
                var info = await client.GetBaseInfoAsync(stack.Id, cancellationToken);
                if (!info.Exists)
                {
                    await CheckpointAsync(
                        stack.Id,
                        ExpressProvisionStatus.WaitingForClient,
                        ExpressProvisionPhase.WaitClient,
                        "Upload a 3.3.5a client or paste a download link, then click Continue.",
                        cancellationToken);
                    return;
                }
            }

            await RunPhaseAsync(scope, stack, phase, cancellationToken);
        }

        await CheckpointAsync(
            stack.Id,
            ExpressProvisionStatus.Completed,
            ExpressProvisionPhase.Done,
            "All ready — press Start!",
            cancellationToken,
            entity =>
            {
                entity.ExpressReadyNoticePending = true;
            });
    }

    private async Task RunPhaseAsync(
        IServiceScope scope,
        ManagedStackEntity stack,
        ExpressProvisionPhase phase,
        CancellationToken cancellationToken)
    {
        var stackId = stack.Id;
        var stacks = scope.ServiceProvider.GetRequiredService<IStackService>();

        switch (phase)
        {
            case ExpressProvisionPhase.SaveChoices:
                await CheckpointAsync(stackId, ExpressProvisionStatus.Running, phase, "Saving Express module choices…", cancellationToken);
                var extras = scope.ServiceProvider.GetRequiredService<IModuleInstallOrchestrator>();
                await extras.SaveChoicesAsync(stackId, new ApplyModuleExtraDataRequest
                {
                    IpContentMode = IpContentMode.ServerWideProgression,
                }, cancellationToken);
                break;

            case ExpressProvisionPhase.DisableBots:
                await CheckpointAsync(stackId, ExpressProvisionStatus.Running, phase, "Disabling playerbots for first boot…", cancellationToken);
                await WritePlayerbotsAsync(scope, stack, enabled: false, randomBotCount: 0, cancellationToken);
                break;

            case ExpressProvisionPhase.StartStack:
                await CheckpointAsync(
                    stackId,
                    ExpressProvisionStatus.Running,
                    phase,
                    "Starting the stack (database, import, then Ollama in the background)…",
                    cancellationToken);
                await stacks.StartAsync(stackId, cancellationToken);
                break;

            case ExpressProvisionPhase.SoapDbc:
                await CheckpointAsync(stackId, ExpressProvisionStatus.Running, phase, "Waiting for SOAP and DBC baseline…", cancellationToken);
                await stacks.InitializeAdminAccountAsync(stackId, cancellationToken);
                var migrations = scope.ServiceProvider.GetRequiredService<IMigrationService>();
                await WaitForDbcBaselineAsync(migrations, stackId, cancellationToken);
                break;

            case ExpressProvisionPhase.AhBot:
                await CheckpointAsync(stackId, ExpressProvisionStatus.Running, phase, "Creating the Auction House bot…", cancellationToken);
                await SetupAhBotAsync(scope, stackId, cancellationToken);
                break;

            case ExpressProvisionPhase.GameAccount:
                await CheckpointAsync(stackId, ExpressProvisionStatus.Running, phase, "Waiting for worldserver SOAP…", cancellationToken);
                await WaitForSoapReadyAsync(scope, stackId, cancellationToken);
                await CheckpointAsync(stackId, ExpressProvisionStatus.Running, phase, "Creating game account admin / admin…", cancellationToken);
                await CreateExpressGameAccountAsync(scope, stackId, cancellationToken);
                await CheckpointAsync(
                    stackId,
                    ExpressProvisionStatus.Running,
                    phase,
                    "Game account admin / admin (GM 3) created.",
                    cancellationToken,
                    entity => entity.ExpressGameAccountCreated = true);
                break;

            case ExpressProvisionPhase.StopStack:
                await CheckpointAsync(stackId, ExpressProvisionStatus.Running, phase, "Stopping the stack…", cancellationToken);
                await stacks.StopAsync(stackId, cancellationToken);
                break;

            case ExpressProvisionPhase.WaitClient:
                // The client files service is started in the WaitClient gate (including while waiting
                // for an upload) so this phase is a no-op once a base client is present.
                break;

            case ExpressProvisionPhase.SwpSync:
                await SyncServerWideProgressionAsync(scope, stackId, cancellationToken);
                break;

            case ExpressProvisionPhase.Addons:
                await InstallSelectedAddonsAsync(scope, stack, cancellationToken);
                break;

            case ExpressProvisionPhase.EnableBots:
                var botCount = Math.Clamp(stack.RandomBotCount, 0, 2500);
                await CheckpointAsync(stackId, ExpressProvisionStatus.Running, phase, "Turning playerbots back on…", cancellationToken);
                await WritePlayerbotsAsync(scope, stack, enabled: true, randomBotCount: botCount, cancellationToken);
                break;

            case ExpressProvisionPhase.Launcher:
                await BuildLauncherAsync(scope, stackId, cancellationToken);
                break;
        }
    }

    private async Task SyncServerWideProgressionAsync(
        IServiceScope scope,
        string stackId,
        CancellationToken cancellationToken)
    {
        var swp = scope.ServiceProvider.GetRequiredService<IServerWideProgressionService>();
        var stacks = scope.ServiceProvider.GetRequiredService<IStackService>();
        await CheckpointAsync(
            stackId,
            ExpressProvisionStatus.Running,
            ExpressProvisionPhase.SwpSync,
            "Starting the client files service…",
            cancellationToken);
        await stacks.StartClientAsync(stackId, forceRecreate: false, cancellationToken);

        await CheckpointAsync(
            stackId,
            ExpressProvisionStatus.Running,
            ExpressProvisionPhase.SwpSync,
            "Importing Server Wide Progression (express-server)…",
            cancellationToken);

        var settings = await swp.GetSettingsAsync(stackId, cancellationToken);
        if (!settings.Bootstrapped)
        {
            try
            {
                await swp.BootstrapAsync(stackId, cancellationToken);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("before any patch", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Skipping SWP bootstrap for stack {StackId}: {Message}", stackId, ex.Message);
            }
        }

        await CheckpointAsync(
            stackId,
            ExpressProvisionStatus.Running,
            ExpressProvisionPhase.SwpSync,
            "Syncing Individual Progression to the express-server mapping…",
            cancellationToken);
        var sync = await swp.RunSyncAsync(stackId, cancellationToken);
        if (!sync.Success)
        {
            throw new InvalidOperationException(sync.Error ?? "Server Wide Progression sync failed.");
        }

        await CheckpointAsync(
            stackId,
            ExpressProvisionStatus.Running,
            ExpressProvisionPhase.SwpSync,
            "Applying the first patch…",
            cancellationToken);
        var migrations = scope.ServiceProvider.GetRequiredService<IMigrationService>();
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
    }

    private async Task BuildLauncherAsync(IServiceScope scope, string stackId, CancellationToken cancellationToken)
    {
        var launcher = scope.ServiceProvider.GetRequiredService<ILauncherBuildService>();
        await CheckpointAsync(
            stackId,
            ExpressProvisionStatus.Running,
            ExpressProvisionPhase.Launcher,
            "Building the launcher…",
            cancellationToken);

        var status = await launcher.StartBuildAsync(LauncherVersionPart.Patch, cancellationToken);
        for (var attempt = 0; attempt < 600; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            status = await launcher.GetStatusAsync(cancellationToken);
            if (!status.IsBuilding)
            {
                break;
            }

            var detail = string.IsNullOrWhiteSpace(status.Message) ? "Building the launcher…" : status.Message;
            await CheckpointAsync(
                stackId,
                ExpressProvisionStatus.Running,
                ExpressProvisionPhase.Launcher,
                detail,
                cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        if (status.Phase == LauncherBuildPhase.Failed)
        {
            throw new InvalidOperationException(status.Error ?? "Launcher build failed.");
        }

        if (!status.DownloadAvailable)
        {
            throw new InvalidOperationException("Launcher build finished but no download is available.");
        }
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

    private static async Task SetupAhBotAsync(IServiceScope scope, string stackId, CancellationToken cancellationToken)
    {
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountManagementService>();
        var result = await accounts.CreateAhBotCharactersAsync(stackId, cancellationToken);
        var guids = new[] { result.AllianceGuid, result.HordeGuid }
            .OrderBy(guid => guid)
            .Select(guid => guid.ToString())
            .ToArray();
        var stacks = scope.ServiceProvider.GetRequiredService<IStackService>();
        await stacks.ApplyModuleConfigAsync(
            stackId,
            new Dictionary<string, string> { [AhBotGuidKey] = string.Join(',', guids) },
            cancellationToken);
    }

    private async Task WaitForSoapReadyAsync(
        IServiceScope scope,
        string stackId,
        CancellationToken cancellationToken)
    {
        var soap = scope.ServiceProvider.GetRequiredService<ISoapProxyService>();
        for (var attempt = 0; attempt < 360; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await soap.IsReachableAsync(stackId, cancellationToken))
            {
                return;
            }

            if (attempt > 0 && attempt % 6 == 0)
            {
                await CheckpointAsync(
                    stackId,
                    ExpressProvisionStatus.Running,
                    ExpressProvisionPhase.GameAccount,
                    "Waiting for worldserver to finish loading before SOAP commands…",
                    cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException(
            "Timed out waiting for the worldserver SOAP interface. The world server container must be running and finished loading.");
    }

    private static async Task CreateExpressGameAccountAsync(
        IServiceScope scope,
        string stackId,
        CancellationToken cancellationToken)
    {
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountManagementService>();
        var soap = scope.ServiceProvider.GetRequiredService<ISoapProxyService>();
        Exception? last = null;
        for (var attempt = 0; attempt < 90; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await soap.IsReachableAsync(stackId, cancellationToken))
            {
                last = new InvalidOperationException("Worldserver SOAP is not reachable yet.");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }

            try
            {
                var (created, message) = await accounts.CreateAccountAsync(
                    stackId,
                    GameAccountUsername,
                    GameAccountPassword,
                    cancellationToken);
                var alreadyExists = !string.IsNullOrWhiteSpace(message)
                    && (message.Contains("already", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("exist", StringComparison.OrdinalIgnoreCase));
                if (!created && !alreadyExists)
                {
                    last = new InvalidOperationException(message ?? "Could not create the admin game account.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                var gm = await accounts.SetGmLevelAsync(stackId, GameAccountUsername, 3, -1, cancellationToken);
                if (!gm)
                {
                    last = new InvalidOperationException("Could not set GM level 3 on all realms for admin.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        throw last ?? new InvalidOperationException("Timed out creating the admin game account via SOAP.");
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

        List<string> moduleIds;
        try
        {
            moduleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson) ?? [];
        }
        catch (JsonException)
        {
            moduleIds = [];
        }

        if (moduleIds.Any(id => string.Equals(id, DungeonClearModuleId, StringComparison.OrdinalIgnoreCase))
            && !addonIds.Contains(DungeonClearAddonId, StringComparer.OrdinalIgnoreCase))
        {
            addonIds.Add(DungeonClearAddonId);
        }

        if (addonIds.Count == 0)
        {
            await CheckpointAsync(
                stack.Id,
                ExpressProvisionStatus.Running,
                ExpressProvisionPhase.Addons,
                "No wizard addons to install.",
                cancellationToken);
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
            await CheckpointAsync(
                stack.Id,
                ExpressProvisionStatus.Running,
                ExpressProvisionPhase.Addons,
                $"Installing addon {name} ({index + 1}/{addonIds.Count})…",
                cancellationToken);
            await addons.InstallFromCatalogAsync(stack.Id, id, cancellationToken);
        }
    }

    private async Task WritePlayerbotsAsync(
        IServiceScope scope,
        ManagedStackEntity stack,
        bool enabled,
        int randomBotCount,
        CancellationToken cancellationToken)
    {
        var stackId = stack.Id;
        var serverConfig = scope.ServiceProvider.GetRequiredService<IServerConfigService>();
        var path = await ResolvePlayerbotsConfPathAsync(scope, serverConfig, stackId, cancellationToken);
        var current = await serverConfig.ReadAsync(stackId, path, cancellationToken);
        var content = ServerConfigValueEditor.SetValue(current.Content, "AiPlayerbot.Enabled", enabled ? "1" : "0");
        content = ServerConfigValueEditor.SetValue(content, "AiPlayerbot.RandomBotAutologin", randomBotCount > 0 ? "1" : "0");
        content = ServerConfigValueEditor.SetValue(content, "AiPlayerbot.MinRandomBots", randomBotCount.ToString());
        content = ServerConfigValueEditor.SetValue(content, "AiPlayerbot.MaxRandomBots", randomBotCount.ToString());
        content = ServerConfigValueEditor.SetValue(
            content,
            "AiPlayerbot.RandomBotAccountCount",
            PlayerbotsRandomBotAccounts.ComputeTotal(randomBotCount, content).ToString());
        if (StackReplacesPlayerbotsChatter(stack))
        {
            foreach (var (key, value) in OllamaSidecar.PlayerbotsChatterDisable)
            {
                content = ServerConfigValueEditor.SetValue(content, key, value);
            }
        }

        await serverConfig.SaveAsync(stackId, path, content, cancellationToken);
    }

    /// <summary>
    /// True when the stack runs an AI chat module that stands in for the playerbots built-in talk.
    /// LLM Chatter does not: it speaks alongside the built-in chatter, so the keys stay untouched.
    /// </summary>
    private static bool StackReplacesPlayerbotsChatter(ManagedStackEntity stack)
    {
        List<string> moduleIds;
        try
        {
            moduleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson) ?? [];
        }
        catch (JsonException)
        {
            return false;
        }

        return moduleIds.Any(OllamaSidecar.ReplacesPlayerbotsChatter);
    }

    private static async Task<string> ResolvePlayerbotsConfPathAsync(
        IServiceScope scope,
        IServerConfigService serverConfig,
        string stackId,
        CancellationToken cancellationToken)
    {
        var files = await serverConfig.ListAsync(stackId, cancellationToken);
        var path = files.Files.FirstOrDefault(file =>
            file.Path.Replace('\\', '/').EndsWith("modules/playerbots.conf", StringComparison.OrdinalIgnoreCase))
            ?.Path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        SeedPlayerbotsConfFromCheckout(scope, stackId);
        files = await serverConfig.ListAsync(stackId, cancellationToken);
        path = files.Files.FirstOrDefault(file =>
            file.Path.Replace('\\', '/').EndsWith("modules/playerbots.conf", StringComparison.OrdinalIgnoreCase))
            ?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("playerbots.conf is not available yet.");
        }

        return path;
    }

    private static void SeedPlayerbotsConfFromCheckout(IServiceScope scope, string stackId)
    {
        var buildsPath = scope.ServiceProvider.GetRequiredService<IOptions<DockerOptions>>().Value.BuildsPath;
        var root = Path.IsPathRooted(buildsPath) ? buildsPath : Path.GetFullPath(buildsPath);
        var dist = Path.Combine(root, stackId, "azerothcore-wotlk", "modules", "mod-playerbots", "conf", "playerbots.conf.dist");
        if (!File.Exists(dist))
        {
            return;
        }

        var etcModules = Path.Combine(root, stackId, "azerothcore-wotlk", "env", "dist", "etc", "modules");
        Directory.CreateDirectory(etcModules);
        var conf = Path.Combine(etcModules, "playerbots.conf");
        if (!File.Exists(conf))
        {
            File.Copy(dist, conf);
        }
    }

    private async Task CheckpointAsync(
        string stackId,
        ExpressProvisionStatus status,
        ExpressProvisionPhase? phase,
        string message,
        CancellationToken cancellationToken,
        Action<ManagedStackEntity>? mutate = null)
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
        if (phase.HasValue)
        {
            stack.ExpressProvisionPhase = phase.Value;
        }

        mutate?.Invoke(stack);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Express provision {StackId}: {Status} / {Phase} — {Message}",
            stackId,
            status,
            stack.ExpressProvisionPhase,
            message);
    }
}
