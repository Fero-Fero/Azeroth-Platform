using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Exceptions;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Persistence-backed stack service.
/// </summary>
public sealed class StackService : IStackService
{
    private static readonly TimeSpan LifecycleVerificationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitContainerVerificationTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LifecyclePollInterval = TimeSpan.FromSeconds(2);

    // The uid/gid the AzerothCore service containers run as (see DOCKER_USER_ID/DOCKER_GROUP_ID below).
    private const int AcoreServiceUid = 1000;
    private const int AcoreServiceGid = 1000;
    private static readonly string[] RequiredRunningServiceNames = ["database", "authserver", "worldserver"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IDockerService _dockerService;
    private readonly IStackDiscoveryService _stackDiscoveryService;
    private readonly IArmoryImageService _armoryImageService;
    private readonly IClientServerImageService _clientServerImageService;
    private readonly ILogger<StackService> _logger;
    private readonly DockerOptions _dockerOptions;
    private readonly ArmoryOptions _armoryOptions;
    private readonly ArmoryAssetsOptions _armoryAssetsOptions;
    private readonly ClientServerOptions _clientServerOptions;
    private readonly ClientDistributionOptions _clientOptions;
    private readonly MigrationOptions _migrationOptions;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly IArmoryJobService _armoryJobService;
    private readonly IManifestSigningKeyProvider _manifestSigningKeys;
    private readonly ISecretProtector _secretProtector;
    private readonly IServerTypeCatalog _serverTypeCatalog;
    private readonly IStackRegistryService _registry;
    private readonly IStackJobService _stackJobService;
    private readonly IStackLauncherService _stackLauncher;
    private readonly IStackImageShippingService _stackImageShipping;

    public StackService(
        AzerothCoreDbContext dbContext, 
        IDockerService dockerService,
        IStackDiscoveryService stackDiscoveryService,
        IArmoryImageService armoryImageService,
        IClientServerImageService clientServerImageService,
        ILogger<StackService> logger,
        IOptions<DockerOptions> dockerOptions,
        IOptions<ArmoryOptions> armoryOptions,
        IOptions<ArmoryAssetsOptions> armoryAssetsOptions,
        IOptions<ClientServerOptions> clientServerOptions,
        IOptions<ClientDistributionOptions> clientOptions,
        IOptions<MigrationOptions> migrationOptions,
        IRemoteEngineService remoteEngine,
        IArmoryJobService armoryJobService,
        IManifestSigningKeyProvider manifestSigningKeys,
        ISecretProtector secretProtector,
        IServerTypeCatalog serverTypeCatalog,
        IStackRegistryService registry,
        IStackJobService stackJobService,
        IStackLauncherService stackLauncher,
        IStackImageShippingService stackImageShipping)
    {
        _dbContext = dbContext;
        _dockerService = dockerService;
        _stackDiscoveryService = stackDiscoveryService;
        _armoryImageService = armoryImageService;
        _clientServerImageService = clientServerImageService;
        _logger = logger;
        _dockerOptions = dockerOptions.Value;
        _armoryOptions = armoryOptions.Value;
        _armoryAssetsOptions = armoryAssetsOptions.Value;
        _clientServerOptions = clientServerOptions.Value;
        _clientOptions = clientOptions.Value;
        _migrationOptions = migrationOptions.Value;
        _remoteEngine = remoteEngine;
        _armoryJobService = armoryJobService;
        _manifestSigningKeys = manifestSigningKeys;
        _secretProtector = secretProtector;
        _serverTypeCatalog = serverTypeCatalog;
        _registry = registry;
        _stackJobService = stackJobService;
        _stackLauncher = stackLauncher;
        _stackImageShipping = stackImageShipping;
    }

    /// <summary>
    /// Best-effort registry rebuild + push so the replicated launcher registry reflects the current set
    /// of visible stacks. Never throws: registry distribution must not fail the triggering operation.
    /// </summary>
    private async Task RepushRegistrySafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _registry.RebuildAndPushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registry re-push failed; it will self-heal on the next trigger.");
        }
    }

    public async Task<IReadOnlyList<StackDetailsDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var stacks = await _dbContext.ManagedStacks
            .OrderByDescending(stack => stack.CreatedAt)
            .ToListAsync(cancellationToken);

        var stackDtos = new List<StackDetailsDto>(stacks.Count);
        foreach (var stack in stacks)
        {
            stackDtos.Add(await MapAsync(stack, cancellationToken));
        }

        return stackDtos;
    }

    public async Task<StackDetailsDto?> GetAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        return stack is null
            ? null
            : await MapAsync(stack, cancellationToken);
    }

    public async Task<StackDetailsDto> CreateAsync(StackConfigurationDto configuration, CancellationToken cancellationToken = default)
    {
        var stackId = Guid.NewGuid().ToString("N");
        var deployment = configuration.Deployment ?? new DeploymentConfigDto();
        var externalHost = (deployment.ExternalHost ?? string.Empty).Trim();

        // Realmlist host resolution: explicit value wins; otherwise for External stacks default to the
        // remote host so clients are pointed at the droplet. Local stacks fall back to the global
        // default (Migrations:RealmlistHost) when left blank.
        var realmlistHost = (configuration.Advanced.RealmlistHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(realmlistHost) && deployment.Target == DeploymentTarget.External)
        {
            realmlistHost = externalHost;
        }

        if (!string.IsNullOrWhiteSpace(realmlistHost))
        {
            realmlistHost = RealmlistHostResolver.ResolveForRealmAddress(realmlistHost, cancellationToken);
        }

        var armoryPort = await AllocateStackPortAsync(cancellationToken, StackNetworkDefaults.DefaultArmoryPort);
        var clientPort = await AllocateStackPortAsync(cancellationToken, StackNetworkDefaults.DefaultClientPort, armoryPort);

        var (serviceEnvJson, worldserverEnvJson) = BuildEnvJson(configuration.Advanced);

        var stack = new ManagedStackEntity
        {
            Id = stackId,
            StackName = configuration.StackName.Trim(),
            NormalizedStackName = NormalizeStackName(configuration.StackName),
            ServerType = configuration.ServerType,
            Status = StackStatus.Stopped,
            ModuleIdsJson = JsonSerializer.Serialize(configuration.ModuleIds, JsonOptions),
            DatabaseRootPassword = configuration.Database.RootPassword,
            DatabasePort = configuration.Database.Port,
            AuthServerPort = configuration.Ports.AuthServer,
            WorldServerPort = configuration.Ports.WorldServer,
            SoapPort = configuration.Ports.SoapPort,
            ArmoryPort = armoryPort,
            // Every stack runs a client-server container that serves client files to launchers.
            ClientPort = clientPort,
            ClientEnabled = true,
            MaxPlayers = configuration.Advanced.MaxPlayers,
            RealmName = configuration.Advanced.RealmName.Trim(),
            CustomEnvVarsJson = worldserverEnvJson,
            ServiceEnvVarsJson = serviceEnvJson,
            SoapUsername = GenerateSoapUsername(stackId),
            SoapPassword = GenerateSecureSoapPassword(),
            RealmlistHostOverride = realmlistHost,
            DeploymentTarget = deployment.Target,
            ExternalHost = externalHost,
            ExternalSshPort = deployment.ExternalSshPort <= 0 ? 22 : deployment.ExternalSshPort,
            ExternalSshUser = (deployment.ExternalSshUser ?? string.Empty).Trim(),
            // Encrypt the SSH private key at rest so a database leak alone cannot use it.
            ExternalSshPrivateKey = _secretProtector.Protect(deployment.ExternalSshPrivateKey),
            CreatedAt = DateTime.UtcNow
        };

        ApplyArmoryEmailSettings(stack, configuration.ArmoryAccounts);

        // For a custom-fork server type, persist the user-supplied core repository/branch up front. The
        // build pipeline prefers a stored CoreRepositoryUrl over the catalog, so this makes it clone the
        // provided fork. Values were already validated by StackConfigurationValidator before create.
        if (_serverTypeCatalog.AllowsCustomRepository(configuration.ServerType)
            && configuration.CustomFork is not null
            && !string.IsNullOrWhiteSpace(configuration.CustomFork.RepositoryUrl))
        {
            stack.CoreRepositoryUrl = ModuleCatalogService.ValidateGitRepository(configuration.CustomFork.RepositoryUrl);
            var customBranch = (configuration.CustomFork.Branch ?? string.Empty).Trim();
            stack.CoreBranch = string.IsNullOrWhiteSpace(customBranch)
                ? "master"
                : ModuleCatalogService.ValidateGitRef(customBranch);
        }

        _dbContext.ManagedStacks.Add(stack);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // For external stacks, provision the SSH docker context up-front so later start/build calls
        // can target the remote engine. Best-effort: creation still succeeds if the remote is offline.
        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            try
            {
                await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to provision remote docker context for external stack {StackId}", stackId);
            }

            try
            {
                var privateKey = _secretProtector.Unprotect(stack.ExternalSshPrivateKey);
                await _remoteEngine.SyncRemoteHostFirewallAsync(
                    stack.ExternalHost,
                    stack.ExternalSshPort,
                    stack.ExternalSshUser,
                    privateKey,
                    new RemoteSetupOptionsDto
                    {
                        RemoteOs = RemoteHostOs.Linux,
                        AuthServerPort = stack.AuthServerPort,
                        WorldServerPort = stack.WorldServerPort,
                        ArmoryPort = stack.ArmoryPort,
                        ClientPort = stack.ClientPort,
                        SshPort = stack.ExternalSshPort
                    },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync VPC host firewall for external stack {StackId}", stackId);
            }
        }

        await RepushRegistrySafeAsync(cancellationToken);
        return await MapAsync(stack, cancellationToken);
    }

    public async Task<StackDetailsDto?> UpdateAsync(string stackId, StackConfigurationDto configuration, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return null;
        }

        EnsureStackLifecycleAllowed(stack, "update");

        var wasRunning = stack.Status == StackStatus.Running;
        var oldModuleIds = Deserialize<List<string>>(stack.ModuleIdsJson) ?? [];
        var newModuleIds = configuration.ModuleIds ?? [];
        var modulesChanged = !oldModuleIds.SequenceEqual(newModuleIds);
        var oldRealmlistHost = ResolveRealmlistHost(stack);

        // Snapshot the armory "Load DBCs" state before overwriting so we can detect an off->on toggle.
        var oldArmoryLoadDbcs = ArmoryLoadDbcsEnabled(stack.ServiceEnvVarsJson);

        // Stop stack if it's running
        if (wasRunning)
        {
            await StopAsync(stackId, cancellationToken);
        }

        // Update database record
        stack.ModuleIdsJson = JsonSerializer.Serialize(configuration.ModuleIds, JsonOptions);
        // The details payload no longer returns the root password (see MapAsync), so a blank value on
        // update means "keep the existing password" rather than wiping it.
        if (!string.IsNullOrWhiteSpace(configuration.Database.RootPassword))
        {
            stack.DatabaseRootPassword = configuration.Database.RootPassword;
        }
        stack.DatabasePort = configuration.Database.Port;
        stack.AuthServerPort = configuration.Ports.AuthServer;
        stack.WorldServerPort = configuration.Ports.WorldServer;
        stack.SoapPort = configuration.Ports.SoapPort;
        stack.MaxPlayers = configuration.Advanced.MaxPlayers;
        stack.RealmName = configuration.Advanced.RealmName.Trim();
        var (updatedServiceEnvJson, updatedWorldserverEnvJson) = BuildEnvJson(configuration.Advanced);
        stack.CustomEnvVarsJson = updatedWorldserverEnvJson;
        stack.ServiceEnvVarsJson = updatedServiceEnvJson;
        stack.RealmlistHostOverride = (configuration.Advanced.RealmlistHost ?? string.Empty).Trim();
        ApplyArmoryEmailSettings(stack, configuration.ArmoryAccounts);

        // Post-create deployment editing: the target itself is fixed (flipping local<->external is a
        // migration, not an edit), but an external stack's connection details can be updated. A blank
        // private key means "keep the existing one" so the UI never has to round-trip the secret.
        if (configuration.Deployment is not null && stack.DeploymentTarget == DeploymentTarget.External)
        {
            var d = configuration.Deployment;
            if (!string.IsNullOrWhiteSpace(d.ExternalHost))
            {
                stack.ExternalHost = d.ExternalHost.Trim();
            }
            if (d.ExternalSshPort > 0)
            {
                stack.ExternalSshPort = d.ExternalSshPort;
            }
            if (!string.IsNullOrWhiteSpace(d.ExternalSshUser))
            {
                stack.ExternalSshUser = d.ExternalSshUser.Trim();
            }
            if (!string.IsNullOrWhiteSpace(d.ExternalSshPrivateKey))
            {
                stack.ExternalSshPrivateKey = _secretProtector.Protect(d.ExternalSshPrivateKey);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        var newRealmlistHost = ResolveRealmlistHost(stack);
        var realmlistHostChanged = !string.Equals(oldRealmlistHost, newRealmlistHost, StringComparison.OrdinalIgnoreCase);

        // Re-provision the SSH docker context so edited connection details take effect immediately.
        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            try
            {
                await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-provision remote docker context for stack {StackId} after update", stackId);
            }
        }

        // Regenerate runtime configuration files if stack has been built
        var stackPath = GetStackPath(stackId);
        var repoPath = Path.Combine(stackPath, "azerothcore-wotlk");
        if (Directory.Exists(repoPath))
        {
            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken);
        }

        // Restart stack if it was running and modules haven't changed
        if (wasRunning && !modulesChanged)
        {
            await StartAsync(stackId, cancellationToken);
        }

        if (realmlistHostChanged)
        {
            await UpdateRealmlistAddressAsync(stack, cancellationToken);
            await RepushRegistrySafeAsync(cancellationToken);
            await RescanStackClientSafeAsync(stack.Id, cancellationToken);
        }

        // If the armory has "Load DBCs" enabled, make sure its DBC dataset is populated from the server.
        // We queue a detached background job (extract server DBCs -> CSV -> rebuild & restart armory) when
        // the flag was just turned on, or it is on but the dataset has no DBC CSVs yet. Subsequent saves
        // with an already-populated dataset are a no-op so we don't rebuild the image on every edit.
        MaybeQueueArmoryDbcSync(stack, oldArmoryLoadDbcs);

        return await MapAsync(stack, cancellationToken);
    }

    public async Task<StackDetailsDto?> ReconnectExternalAsync(
        string stackId,
        DeploymentConfigDto deployment,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return null;
        }

        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            throw new InvalidOperationException("Only external stacks can be reconnected.");
        }

        if (string.IsNullOrWhiteSpace(deployment.ExternalHost)
            || string.IsNullOrWhiteSpace(deployment.ExternalSshUser)
            || string.IsNullOrWhiteSpace(deployment.ExternalSshPrivateKey))
        {
            throw new InvalidOperationException("Remote host, SSH user, and private key are required to reconnect.");
        }

        var test = await _remoteEngine.TestConnectionAsync(
            deployment.ExternalHost.Trim(),
            deployment.ExternalSshPort <= 0 ? 22 : deployment.ExternalSshPort,
            deployment.ExternalSshUser.Trim(),
            deployment.ExternalSshPrivateKey,
            cancellationToken: cancellationToken);
        if (!test.Success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(test.Message)
                ? "Remote connection test failed."
                : test.Message);
        }

        stack.ExternalHost = deployment.ExternalHost.Trim();
        stack.ExternalSshPort = deployment.ExternalSshPort <= 0 ? 22 : deployment.ExternalSshPort;
        stack.ExternalSshUser = deployment.ExternalSshUser.Trim();
        stack.ExternalSshPrivateKey = _secretProtector.Protect(deployment.ExternalSshPrivateKey);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
        _logger.LogInformation("Reconnected external stack {StackId} to {Host}.", stackId, stack.ExternalHost);
        return await MapAsync(stack, cancellationToken);
    }

    public async Task<bool> ApplyStackPublicHostAsync(
        string stackId, string host, CancellationToken cancellationToken = default)
    {
        host = (host ?? string.Empty).Trim();
        if (host.Length is < 1 or > 255)
        {
            throw new ArgumentException("Stack public host must be between 1 and 255 characters.", nameof(host));
        }

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return false;
        }

        EnsureStackLifecycleAllowed(stack, "update the public host of");

        stack.RealmlistHostOverride = host;

        // If the operator chose a concrete machine IP, also clear stale armory/client bind addresses by
        // binding those player-facing HTTP ports to the same current interface. DNS names remain realmlist
        // hosts only; Docker port publishing cannot bind to a hostname.
        if (System.Net.IPAddress.TryParse(host, out _))
        {
            stack.PublishBindAddress = host;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        if (Directory.Exists(repoPath))
        {
            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken);
            await RecreatePublicHostServicesAsync(stack, repoPath, cancellationToken);
        }

        await UpdateRealmlistAddressAsync(stack, cancellationToken);
        await RepushRegistrySafeAsync(cancellationToken);
        await RescanStackClientSafeAsync(stack.Id, cancellationToken);

        return true;
    }

    /// <summary>
    /// Queues an armory DBC-sync background job when the stack's armory has "Load DBCs" enabled and the
    /// DBC dataset needs (re)populating. No-op when the armory is disabled or the flag is off.
    /// </summary>
    private void MaybeQueueArmoryDbcSync(ManagedStackEntity stack, bool wasLoadDbcsEnabled)
    {
        if (!stack.ArmoryEnabled)
        {
            return;
        }

        var nowEnabled = ArmoryLoadDbcsEnabled(stack.ServiceEnvVarsJson);
        if (!nowEnabled)
        {
            return;
        }

        var dbcDir = Path.Combine(_armoryAssetsOptions.DataPathFor(stack.Id), "dbc");
        var datasetPopulated = Directory.Exists(dbcDir) && Directory.EnumerateFiles(dbcDir, "*.csv").Any();

        if (!wasLoadDbcsEnabled || !datasetPopulated)
        {
            _logger.LogInformation("Queuing armory DBC sync for stack {StackId} (Load DBCs enabled).", stack.Id);
            _armoryJobService.Enqueue(stack.Id, ArmoryJobAction.SyncDbc);
        }
    }

    private async Task RescanStackClientSafeAsync(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            await _stackLauncher.RescanAsync(stackId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rescan launcher client after realmlist change for stack {StackId}.", stackId);
        }
    }

    private async Task RecreatePublicHostServicesAsync(
        ManagedStackEntity stack, string repoPath, CancellationToken cancellationToken)
    {
        var containers = await GetContainersAsync(stack.Id, cancellationToken);
        var services = new List<string>();

        if (containers.Any(c =>
                c.Name.Contains("authserver", StringComparison.OrdinalIgnoreCase)
                && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase)))
        {
            services.Add("ac-authserver");
        }

        if (containers.Any(c =>
                c.Name.Contains("worldserver", StringComparison.OrdinalIgnoreCase)
                && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase)))
        {
            services.Add("ac-worldserver");
        }

        if (stack.ArmoryEnabled && containers.Any(c =>
                c.Name.EndsWith("-armory", StringComparison.OrdinalIgnoreCase)
                && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase)))
        {
            services.Add("frontend-armory");
        }

        if (stack.ArmoryEnabled && containers.Any(c =>
                c.Name.EndsWith("-armory-assets", StringComparison.OrdinalIgnoreCase)
                && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase)))
        {
            services.Add("armory-assets");
        }

        if (stack.ClientEnabled && containers.Any(c =>
                c.Name.EndsWith("-client", StringComparison.OrdinalIgnoreCase)
                && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase)))
        {
            services.Add("client");
        }

        if (services.Count == 0)
        {
            return;
        }

        await RunDockerComposeAsync(
            stack.Id,
            $"up -d --force-recreate --no-deps {string.Join(' ', services.Distinct())}",
            repoPath,
            cancellationToken);
    }

    /// <summary>
    /// Reads the effective armory <c>ACORE_ARMORY_LOAD_DBCS</c> flag from a per-service env JSON blob.
    /// Absent means the template default (enabled); a stored override wins.
    /// </summary>
    private static bool ArmoryLoadDbcsEnabled(string? serviceEnvJson)
    {
        var perService = Deserialize<Dictionary<string, Dictionary<string, string>>>(serviceEnvJson ?? string.Empty)
            ?? new Dictionary<string, Dictionary<string, string>>();

        var value = perService.TryGetValue(DockerComposeOverrideGenerator.ArmoryService, out var bucket)
            && bucket is not null
            && bucket.TryGetValue("ACORE_ARMORY_LOAD_DBCS", out var raw)
                ? raw
                : "1"; // Template default is enabled.

        return value.Trim() is "1" or "true" or "True" or "yes" or "on";
    }

    public async Task<bool> DeleteAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        var stackPath = GetStackPath(stackId);
        var repoPath = Path.Combine(stackPath, "azerothcore-wotlk");

        // Stop containers if running
        try
        {
            if (Directory.Exists(repoPath))
            {
                await RunDockerComposeAsync(stackId, "down -v", repoPath, cancellationToken);
            }
        }
        catch
        {
            // Container might not exist, continue with cleanup
        }

        // Remove Docker images and all per-stack volumes/containers on the engine (local or remote).
        await CleanupStackDockerFootprintAsync(stack, cancellationToken);

        // External stacks: also remove the SSH docker context/key material.
        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            try
            {
                await _remoteEngine.RemoveContextAsync(stack, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove remote docker context for external stack {StackId}", stack.Id);
            }
        }

        CleanupManagerPersistentData(stackId);

        // Remove stack directory (gracefully handle if already removed)
        if (Directory.Exists(stackPath))
        {
            try
            {
                Directory.Delete(stackPath, recursive: true);
            }
            catch (IOException)
            {
                // Directory might be in use or already removed, continue
            }
            catch (UnauthorizedAccessException)
            {
                // Permission issue, continue anyway
            }
        }

        // Remove from database
        _dbContext.ManagedStacks.Remove(stack);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Drop the stack from the replicated registry on the remaining visible stacks.
        await RepushRegistrySafeAsync(cancellationToken);
        return true;
    }

    public async Task<bool> StartAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        EnsureStackLifecycleAllowed(stack, "start");

        var stackPath = GetStackPath(stackId);
        if (!Directory.Exists(stackPath))
        {
            throw new InvalidOperationException($"Stack directory not found: {stackPath}");
        }

        var repoPath = Path.Combine(stackPath, "azerothcore-wotlk");
        stack.Status = StackStatus.Starting;
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            // Start the armory alongside the stack. Best-effort: if the image can't be built we
            // simply omit it from the compose so the game servers still start.
            var armoryReady = await TryEnsureArmoryImageAsync(stack.Id, cancellationToken);
            stack.ArmoryEnabled = armoryReady;

            // Same for the per-stack client-server: build/ensure the shared image (on the stack's
            // engine) and only render it into the compose when it's actually available.
            var clientReady = stack.ClientEnabled && await TryEnsureClientImageAsync(stack, cancellationToken);

            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken, includeArmory: armoryReady, includeClient: clientReady);
            await ShipExternalStackImagesAsync(stack, armoryReady, clientReady, cancellationToken);
            await BringStackUpAsync(stack, stackId, repoPath, armoryReady, clientReady, cancellationToken);
            await WaitForRunningServicesAsync(stackId, cancellationToken);
            await UpdateRealmlistAddressAsync(stack, cancellationToken);

            stack.Status = StackStatus.Running;
            await _dbContext.SaveChangesAsync(cancellationToken);

            // A freshly-started client container only has its env fallback portal until it receives the
            // replicated registry; push it now so the stack self-heals into the launcher's server list.
            if (clientReady)
            {
                await RepushRegistrySafeAsync(cancellationToken);
            }

            return true;
        }
        catch
        {
            stack.Status = StackStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> SetPostBuildActionAsync(string stackId, PostBuildAction action, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        stack.PostBuildAction = action;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetConfigMigrationModeAsync(string stackId, ConfigMigrationMode mode, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        stack.ConfigMigrationMode = mode;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> StartDatabaseAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        EnsureStackLifecycleAllowed(stack, "start the database of");

        var stackPath = GetStackPath(stackId);
        if (!Directory.Exists(stackPath))
        {
            throw new InvalidOperationException($"Stack directory not found: {stackPath}");
        }

        var repoPath = Path.Combine(stackPath, "azerothcore-wotlk");
        stack.Status = StackStatus.Starting;
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken);
            await RunDockerComposeAsync(stackId, "up -d ac-database", repoPath, cancellationToken);
            await WaitForDatabaseServiceAsync(stackId, cancellationToken);

            // Ensure the game servers are down so maintenance/migrations run without interruption.
            // Safe whether starting from Stopped (no-op) or from a Running stack (DB Maintenance).
            await RunDockerComposeAsync(stackId, "stop ac-worldserver ac-authserver", repoPath, cancellationToken);

            // Only the database is intentionally up, so the stack is partially operational.
            stack.Status = StackStatus.Degraded;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch
        {
            stack.Status = StackStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> StopAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        EnsureStackLifecycleAllowed(stack, "stop");

        var stackPath = GetStackPath(stackId);
        if (!Directory.Exists(stackPath))
        {
            throw new InvalidOperationException($"Stack directory not found: {stackPath}");
        }

        var repoPath = Path.Combine(stackPath, "azerothcore-wotlk");
        try
        {
            await RunDockerComposeAsync(stackId, "down", repoPath, cancellationToken);
            await WaitForStackToStopAsync(stackId, cancellationToken);

            // `down` also removes the armory container.
            stack.ArmoryEnabled = false;
            stack.Status = StackStatus.Stopped;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch
        {
            stack.Status = StackStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> RestartAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        EnsureStackLifecycleAllowed(stack, "restart");

        var stackPath = GetStackPath(stackId);
        if (!Directory.Exists(stackPath))
        {
            throw new InvalidOperationException($"Stack directory not found: {stackPath}");
        }

        var repoPath = Path.Combine(stackPath, "azerothcore-wotlk");
        stack.Status = StackStatus.Starting;
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var armoryReady = await TryEnsureArmoryImageAsync(stack.Id, cancellationToken);
            stack.ArmoryEnabled = armoryReady;
            var clientReady = stack.ClientEnabled && await TryEnsureClientImageAsync(stack, cancellationToken);

            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken, includeArmory: armoryReady, includeClient: clientReady);
            await ShipExternalStackImagesAsync(stack, armoryReady, clientReady, cancellationToken);
            await BringStackUpAsync(stack, stackId, repoPath, armoryReady, clientReady, cancellationToken);
            await WaitForRunningServicesAsync(stackId, cancellationToken);
            await UpdateRealmlistAddressAsync(stack, cancellationToken);

            stack.Status = StackStatus.Running;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch
        {
            stack.Status = StackStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> RestartServerProcessesAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        if (!Directory.Exists(repoPath))
        {
            throw new InvalidOperationException("Stack has not been built yet.");
        }

        // Regenerate .env + override (adds/refreshes the lua_scripts mount) then force-recreate the
        // game servers so they re-read config files and load newly-added Lua scripts.
        await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken);
        await RunDockerComposeAsync(
            stackId,
            "up -d --force-recreate ac-worldserver ac-authserver",
            repoPath,
            cancellationToken);

        return true;
    }

    public async Task<ArmoryNetworkConfigDto?> GetArmoryNetworkAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return null;
        }

        var containers = await GetContainersAsync(stackId, cancellationToken);
        var armoryRunning = containers.Any(c =>
            c.Name.Contains("armory", StringComparison.OrdinalIgnoreCase)
            && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase));

        return BuildArmoryNetworkDto(stack, armoryRunning);
    }

    public async Task<ArmoryNetworkConfigDto?> UpdateArmoryNetworkAsync(
        string stackId, ArmoryNetworkConfigDto config, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return null;
        }

        EnsureStackLifecycleAllowed(stack, "update the network settings of");

        var bindAddress = NormalizeBindAddress(config.BindAddress);
        var armoryPort = await ValidateStackPortAsync(stack, config.ArmoryPort, "Armory", config.ClientPort, cancellationToken);
        var clientPort = await ValidateStackPortAsync(stack, config.ClientPort, "Client", armoryPort, cancellationToken);

        stack.ArmoryPort = armoryPort;
        stack.ClientPort = clientPort;
        stack.PublishBindAddress = bindAddress;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Regenerate the runtime artifacts (.env + override) so the new ports/bind are written, then
        // force-recreate the player-facing HTTP containers if the stack is up so the change is live.
        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        if (Directory.Exists(repoPath))
        {
            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken);

            var containers = await GetContainersAsync(stackId, cancellationToken);
            var services = new List<string>();
            if (stack.ArmoryEnabled && containers.Any(c =>
                    c.Name.Contains("armory", StringComparison.OrdinalIgnoreCase)
                    && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase)))
            {
                // armory-assets is the sidecar the armory serves model-viewer files from; recreate together.
                services.Add("frontend-armory");
                services.Add("armory-assets");
            }
            if (stack.ClientEnabled && containers.Any(c =>
                    c.Name.EndsWith("-client", StringComparison.OrdinalIgnoreCase)
                    && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase)))
            {
                services.Add("client");
            }

            if (services.Count > 0)
            {
                foreach (var service in services)
                {
                    await PrepareFixedNameServiceRecreateAsync(stackId, stack, service, repoPath, cancellationToken);
                }

                await RunDockerComposeAsync(
                    stackId,
                    $"up -d --force-recreate --no-deps {string.Join(' ', services)}",
                    repoPath,
                    cancellationToken);
            }
        }

        // Port/bind changes affect portal.json URLs for every stack; push the full registry snapshot.
        await RepushRegistrySafeAsync(cancellationToken);
        await RescanStackClientSafeAsync(stackId, cancellationToken);
        await TrySyncExternalWebFirewallAsync(stack, cancellationToken);

        return await GetArmoryNetworkAsync(stackId, cancellationToken);
    }

    private ArmoryNetworkConfigDto BuildArmoryNetworkDto(ManagedStackEntity stack, bool armoryRunning)
    {
        var localStack = stack.DeploymentTarget != DeploymentTarget.External;
        string effectiveBind;
        if (!string.IsNullOrWhiteSpace(stack.PublishBindAddress))
        {
            effectiveBind = stack.PublishBindAddress.Trim();
        }
        else if (localStack)
        {
            effectiveBind = string.IsNullOrWhiteSpace(_dockerOptions.PublishBindAddress)
                ? "127.0.0.1"
                : _dockerOptions.PublishBindAddress.Trim();
        }
        else
        {
            effectiveBind = "0.0.0.0";
        }

        return new ArmoryNetworkConfigDto
        {
            ArmoryPort = stack.ArmoryPort,
            ClientPort = stack.ClientPort,
            BindAddress = stack.PublishBindAddress,
            EffectiveBindAddress = effectiveBind,
            IsLocalDeployment = localStack,
            ArmoryRunning = armoryRunning,
        };
    }

    // Blank (inherit), "0.0.0.0", or a valid IP address. Anything else is rejected so an operator can't
    // brick port publishing with a hostname docker can't bind to.
    private static string NormalizeBindAddress(string? bindAddress)
    {
        var trimmed = (bindAddress ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }
        if (!System.Net.IPAddress.TryParse(trimmed, out _))
        {
            throw new ArgumentException(
                $"'{trimmed}' is not a valid bind address. Use a numeric IP (e.g. 0.0.0.0 for all interfaces, 127.0.0.1 for this machine only) or leave it blank to inherit the default.");
        }
        return trimmed;
    }

    // Validates a host port is in a safe range and not already taken by another stack (or this stack's
    // other services / the sibling port being set in the same request).
    private async Task<int> ValidateStackPortAsync(
        ManagedStackEntity stack, int port, string label, int siblingPort, CancellationToken cancellationToken)
    {
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentException($"{label} port must be between 1024 and 65535.");
        }
        if (port == siblingPort)
        {
            throw new ArgumentException("The armory and client ports must be different.");
        }
        // This stack's own game/data ports must not collide with the web ports.
        if (port == stack.DatabasePort || port == stack.AuthServerPort
            || port == stack.WorldServerPort || port == stack.SoapPort)
        {
            throw new ArgumentException($"{label} port {port} conflicts with another service in this stack.");
        }

        var used = await _dbContext.ManagedStacks
            .Where(s => s.Id != stack.Id)
            .Select(s => new { s.ArmoryPort, s.ClientPort, s.DatabasePort, s.WorldServerPort, s.AuthServerPort, s.SoapPort })
            .ToListAsync(cancellationToken);
        if (used.Any(r => r.ArmoryPort == port || r.ClientPort == port || r.DatabasePort == port
            || r.WorldServerPort == port || r.AuthServerPort == port || r.SoapPort == port))
        {
            throw new ArgumentException($"{label} port {port} is already used by another stack.");
        }

        return port;
    }

    public Task<bool> StartArmoryAsync(string stackId, CancellationToken cancellationToken = default) =>
        StartArmoryInternalAsync(stackId, forceRecreate: false);

    /// <summary>
    /// Shared armory start implementation. When <paramref name="forceRecreate"/> is true the armory
    /// container is recreated (<c>--force-recreate</c>) so config/env changes are actually applied;
    /// a plain <c>up -d</c> on an already-running armory would otherwise be a no-op.
    /// <para>
    /// Runs on <see cref="CancellationToken.None"/> by design: the image (re)build + container start
    /// mutate Docker/compose state and must run to completion even if the HTTP caller disconnects
    /// (e.g. a browser refresh aborts the request, tripping <c>HttpContext.RequestAborted</c>). Tying
    /// this to the request token risks leaving the armory image or container half-built.
    /// </para>
    /// </summary>
    private async Task<bool> StartArmoryInternalAsync(string stackId, bool forceRecreate)
    {
        // Detached from any request-scoped token on purpose (see remarks above).
        var cancellationToken = CancellationToken.None;

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        if (!Directory.Exists(repoPath))
        {
            throw new InvalidOperationException("Stack has not been built yet.");
        }

        // Build the image up-front (surface failures to the caller), then render the armory into
        // the override and bring just that service up. "Rebuild & Restart" (forceRecreate) rebuilds
        // the image from source so armory code changes are picked up; a plain Start reuses the cached
        // image if present.
        if (forceRecreate)
        {
            await _armoryImageService.RebuildImageAsync(stackId, cancellationToken);
        }
        else
        {
            await _armoryImageService.EnsureImageAsync(stackId, cancellationToken);
        }
        stack.ArmoryEnabled = true;
        await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken, includeArmory: true);
        await ShipExternalStackImagesAsync(stack, includeArmory: true, includeClient: false, cancellationToken);

        // The armory reads the stack's auth/characters/world databases, so the DB must be up.
        // If it isn't running, start just the database (and wait for it) before the armory.
        var containers = await GetContainersAsync(stackId, cancellationToken);
        var databaseRunning = containers.Any(container =>
            container.Name.Contains("database", StringComparison.OrdinalIgnoreCase) && IsRunning(container));
        if (!databaseRunning)
        {
            _logger.LogInformation("Armory start requested for stack {StackId} but its database is not running; starting the database first.", stackId);
            await RunDockerComposeAsync(stackId, "up -d ac-database", repoPath, cancellationToken);
            await WaitForDatabaseServiceAsync(stackId, cancellationToken);
        }

        var recreate = forceRecreate ? " --force-recreate" : string.Empty;
        if (forceRecreate)
        {
            await PrepareFixedNameServiceRecreateAsync(stackId, stack, "frontend-armory", repoPath, cancellationToken);
        }

        await RunDockerComposeAsync(stackId, $"up -d{recreate} frontend-armory", repoPath, cancellationToken);

        try
        {
            await _armoryImageService.SyncLiveLayoutAsync(stackId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync live armory layout after starting armory for stack {StackId}.", stackId);
        }

        await TrySyncExternalWebFirewallAsync(stack, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> StopArmoryAsync(string stackId, CancellationToken cancellationToken = default)
    {
        // Detach from the caller's token: tearing down the armory containers and regenerating the
        // compose override must finish even if the HTTP caller disconnects (e.g. a page refresh),
        // otherwise the stack can be left with a stale override or half-removed containers.
        cancellationToken = CancellationToken.None;

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        stack.ArmoryEnabled = false;
        if (Directory.Exists(repoPath))
        {
            // stop + rm so the containers disappear from the stack view; the stack itself is untouched.
            // The asset sidecar (armory-assets) is torn down with the armory it serves.
            await RunDockerComposeAsync(stackId, "rm -sf frontend-armory armory-assets", repoPath, cancellationToken);
            // Regenerate the override without the armory so a later full `up -d` won't recreate it.
            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken, includeArmory: false);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Best-effort build of the shared armory image. Returns false (and logs) if the image can't be
    /// built, so callers can start the stack without the armory rather than failing outright.
    /// </summary>
    /// <summary>
    /// Brings a stack up. External stacks use an explicit init → game-server sequence because
    /// <c>docker --context … compose up -d</c> does not reliably chain one-shot init containers to
    /// auth/world on a remote engine.
    /// </summary>
    private async Task BringStackUpAsync(
        ManagedStackEntity stack,
        string stackId,
        string repoPath,
        bool armoryReady,
        bool clientReady,
        CancellationToken cancellationToken)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            await RunDockerComposeAsync(stackId, "up -d", repoPath, cancellationToken);
            return;
        }

        var containerPrefix = DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName);

        await RunDockerComposeAsync(stackId, "up -d ac-database", repoPath, cancellationToken);
        await WaitForDatabaseServiceAsync(stackId, cancellationToken);

        await RunDockerComposeAsync(stackId, "up -d ac-db-import ac-client-data-init", repoPath, cancellationToken);
        await WaitForInitContainerAsync(stackId, $"{containerPrefix}-db-import", "DB import", cancellationToken);
        await WaitForInitContainerAsync(stackId, $"{containerPrefix}-client-data-init", "Client data init", cancellationToken);

        // db-import hammers MySQL; wait until it accepts connections again before game servers start.
        await WaitForDatabaseReadyAsync(stack, stackId, cancellationToken);

        // Auth validates that realmlist.address resolves from inside its container — set the row before
        // auth/world start, and store a literal IP (not an EC2 hostname Docker DNS cannot resolve).
        await UpdateRealmlistAddressAsync(stack, cancellationToken);

        var services = new List<string> { "ac-authserver", "ac-worldserver" };
        if (armoryReady)
        {
            var armoryOptions = BuildArmoryComposeOptions(stack);
            if (armoryOptions.AssetsAvailable)
            {
                services.Add("armory-assets");
            }

            services.Add("frontend-armory");
        }

        if (clientReady)
        {
            services.Add("client");
        }

        await RunDockerComposeAsync(stackId, $"up -d {string.Join(' ', services)}", repoPath, cancellationToken);
    }

    /// <summary>Waits for a one-shot init container to exit successfully on the stack's engine.</summary>
    private async Task WaitForInitContainerAsync(
        string stackId,
        string containerName,
        string displayName,
        CancellationToken cancellationToken)
    {
        var contextArg = await GetDockerContextArgAsync(stackId, cancellationToken);
        var deadline = DateTime.UtcNow + InitContainerVerificationTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"{contextArg}inspect -f \"{{{{.State.Status}}}}|{{{{.State.ExitCode}}}}\" {containerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is not null)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode == 0)
                {
                    var parts = stdout.Trim().Split('|');
                    var status = parts.Length > 0 ? parts[0].Trim() : string.Empty;

                    if (status.Equals("exited", StringComparison.OrdinalIgnoreCase))
                    {
                        var code = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var parsed) ? parsed : -1;
                        if (code != 0)
                        {
                            throw new InvalidOperationException($"{displayName} failed with exit code {code}.");
                        }

                        return;
                    }
                }
            }

            await Task.Delay(LifecyclePollInterval, cancellationToken);
        }

        throw new InvalidOperationException($"{displayName} did not complete before the startup timeout elapsed.");
    }

    private async Task ShipExternalStackImagesAsync(
        ManagedStackEntity stack,
        bool includeArmory,
        bool includeClient,
        CancellationToken cancellationToken)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            return;
        }

        _logger.LogInformation("Shipping stack images to remote engine before compose up (stack {StackId}).", stack.Id);
        await _stackImageShipping.ShipStackImagesAsync(stack, includeArmory, includeClient, cancellationToken);
    }

    private async Task<bool> TryEnsureArmoryImageAsync(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            await _armoryImageService.EnsureImageAsync(stackId, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Armory image unavailable; the stack will start without the armory.");
            return false;
        }
    }

    /// <summary>
    /// Ensures the shared client-server image exists on the stack's engine (local or, for external
    /// stacks, the remote via docker context). Best-effort: on failure the stack starts without the
    /// client-server container, so the game servers still come up.
    /// </summary>
    private async Task<bool> TryEnsureClientImageAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        try
        {
            // External stacks build the client image locally on the manager, then ship it to the remote
            // engine; building via a remote docker context would stream a large context over SSH for no benefit.
            await _clientServerImageService.EnsureImageAsync(dockerContext: null, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Client-server image unavailable; the stack will start without the client container.");
            return false;
        }
    }

    private Task<int> AllocateArmoryPortAsync(CancellationToken cancellationToken)
        => AllocateStackPortAsync(cancellationToken);

    /// <summary>
    /// Picks a host port for a new stack service (armory, client, ...) that does not collide with any
    /// port already used by an existing stack (armory, client, database, world, auth, or SOAP) nor with
    /// any port passed in <paramref name="alsoExclude"/> (ports allocated earlier in the same request).
    /// </summary>
    private async Task<int> AllocateStackPortAsync(
        CancellationToken cancellationToken,
        int? preferredPort = null,
        params int[] alsoExclude)
    {
        var used = new HashSet<int>(alsoExclude);
        var rows = await _dbContext.ManagedStacks
            .Select(s => new { s.ArmoryPort, s.ClientPort, s.DatabasePort, s.WorldServerPort, s.AuthServerPort, s.SoapPort })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            used.Add(row.ArmoryPort);
            used.Add(row.ClientPort);
            used.Add(row.DatabasePort);
            used.Add(row.WorldServerPort);
            used.Add(row.AuthServerPort);
            used.Add(row.SoapPort);
        }

        if (preferredPort is > 0 && !used.Contains(preferredPort.Value))
        {
            return preferredPort.Value;
        }

        for (var port = StackNetworkDefaults.PortRangeStart; port < StackNetworkDefaults.PortRangeEnd; port++)
        {
            if (!used.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException(
            $"No free stack port available in range {StackNetworkDefaults.PortRangeStart}-{StackNetworkDefaults.PortRangeEnd - 1}.");
    }

    private string GetStackPath(string stackId)
    {
        var configuredPath = _dockerOptions.BuildsPath;
        var baseDir = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath);

        return Path.Combine(baseDir, stackId);
    }

    private async Task RunDockerComposeAsync(string stackId, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var composeProjectName = DockerComposeOverrideGenerator.GetComposeProjectName(stackId);
        var dockerContext = await ResolveDockerContextAsync(stackId, cancellationToken);

        string command;
        string fullArgs;
        if (dockerContext is not null)
        {
            // External stacks always use `docker --context <ctx> compose ...` (compose v2) so the
            // command runs against the remote engine over SSH.
            command = "docker";
            fullArgs = $"--context {dockerContext} compose --project-name {composeProjectName} {arguments}";
        }
        else
        {
            var (localCommand, argPrefix) = DockerComposeHelper.GetDockerCompose(_dockerOptions.ComposeCommand);
            command = localCommand;
            fullArgs = string.IsNullOrEmpty(argPrefix)
                ? $"--project-name {composeProjectName} {arguments}"
                : $"{argPrefix} --project-name {composeProjectName} {arguments}";
        }
        
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = fullArgs,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["COMPOSE_PROJECT_NAME"] = composeProjectName;

        using var process = new Process { StartInfo = startInfo };
        var stdout = new List<string>();
        var stderr = new List<string>();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                stdout.Add(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                stderr.Add(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {command} process");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var errorOutput = stderr.Count > 0
                ? string.Join(Environment.NewLine, stderr)
                : string.Join(Environment.NewLine, stdout);

            throw new InvalidOperationException($"{command} {fullArgs} failed: {errorOutput}");
        }
    }

    /// <summary>
    /// Removes compose-managed containers before <c>--force-recreate</c>. Stacks pin
    /// <c>container_name</c> in the override; compose's in-place recreate can leave a stale container
    /// holding that name and fail with "name is already in use".
    /// </summary>
    private async Task PrepareFixedNameServiceRecreateAsync(
        string stackId,
        ManagedStackEntity stack,
        string composeService,
        string repoPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunDockerComposeAsync(stackId, $"rm -sf {composeService}", repoPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Best-effort compose rm before recreate of {Service} on stack {StackId}.",
                composeService,
                stackId);
        }

        var containerName = DockerComposeOverrideGenerator.GetContainerNameForService(
            stack.Id, stack.StackName, composeService);
        if (containerName is null)
        {
            return;
        }

        var contextArg = await BuildDockerContextArgumentAsync(stackId, cancellationToken);
        await RunDockerBestEffortAsync($"{contextArg}rm -f {containerName}", cancellationToken);
    }

    private async Task<string> BuildDockerContextArgumentAsync(string stackId, CancellationToken cancellationToken)
    {
        var dockerContext = await ResolveDockerContextAsync(stackId, cancellationToken);
        return dockerContext is null ? string.Empty : $"--context {dockerContext} ";
    }

    /// <summary>
    /// Returns the SSH docker context name for an external stack, or null for local stacks (which use
    /// the default local engine). The context is (re)created on demand so it survives platform restarts.
    /// </summary>
    private async Task<string?> ResolveDockerContextAsync(string stackId, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (stack is null || stack.DeploymentTarget != DeploymentTarget.External)
        {
            return null;
        }

        return await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
    }

    /// <summary>
    /// Docker context for an already-loaded stack entity (avoids the reload in the by-id overload):
    /// the external SSH context, or null for local stacks.
    /// </summary>
    private async Task<string?> ResolveDockerContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken) =>
        stack.DeploymentTarget != DeploymentTarget.External
            ? null
            : await _remoteEngine.EnsureContextAsync(stack, cancellationToken);

    /// <summary>Docker CLI argument prefix that targets the stack's engine ("" for local, "--context ... " for external).</summary>
    private async Task<string> GetDockerContextArgAsync(string stackId, CancellationToken cancellationToken)
    {
        var context = await ResolveDockerContextAsync(stackId, cancellationToken);
        return context is null ? string.Empty : $"--context {context} ";
    }

    private string ResolveRealmlistHost(ManagedStackEntity stack) =>
        string.IsNullOrWhiteSpace(stack.RealmlistHostOverride) ? _migrationOptions.RealmlistHost : stack.RealmlistHostOverride;

    /// <summary>
    /// Idempotently rewrites the acore_auth.realmlist row (id 1) so the auth server hands connecting
    /// clients the correct world address/port. Without this the upstream db-import default of
    /// 127.0.0.1:8085 is served and non-local clients cannot connect even after a successful login.
    /// Best-effort: a failure here is logged but does not fail the stack start.
    /// </summary>
    private async Task UpdateRealmlistAddressAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        var host = ResolveRealmlistHost(stack);
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogWarning("No realmlist host resolved for stack {StackId}; leaving DB realmlist untouched.", stack.Id);
            return;
        }

        var realmAddress = RealmlistHostResolver.ResolveForRealmAddress(host, cancellationToken);
        if (!string.Equals(host, realmAddress, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Resolved realmlist host {Host} to {Address} for stack {StackId} (auth containers require a resolvable address).",
                host,
                realmAddress,
                stack.Id);

            if (string.Equals(stack.RealmlistHostOverride, host, StringComparison.OrdinalIgnoreCase))
            {
                stack.RealmlistHostOverride = realmAddress;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        try
        {
            var composeProjectName = DockerComposeOverrideGenerator.GetComposeProjectName(stack.Id);
            var dockerContext = await ResolveDockerContextAsync(stack, cancellationToken);
            var containers = await _dockerService.ListContainersAsync(composeProjectName, dockerContext, cancellationToken);
            var databaseContainer = containers
                .FirstOrDefault(c => c.Name.Contains("database", StringComparison.OrdinalIgnoreCase));

            if (databaseContainer is null)
            {
                _logger.LogWarning("Database container not found for stack {StackId}; skipping realmlist update.", stack.Id);
                return;
            }

            var realmName = string.IsNullOrWhiteSpace(stack.RealmName) ? "AzerothCore" : stack.RealmName;
            var sql =
                $"UPDATE acore_auth.realmlist SET address='{EscapeSqlLiteral(realmAddress)}', " +
                $"localAddress='{EscapeSqlLiteral(realmAddress)}', localSubnetMask='255.255.255.0', " +
                $"port={stack.WorldServerPort}, name='{EscapeSqlLiteral(realmName)}' WHERE id=1;";

            var contextArg = await GetDockerContextArgAsync(stack.Id, cancellationToken);
            var arguments =
                $"{contextArg}exec -i {databaseContainer.Name} mysql -uroot " +
                $"-p{stack.DatabaseRootPassword} -e \"{sql.Replace("\"", "\\\"")}\"";

            var (exitCode, _, error) = await RunDockerCliAsync(arguments, cancellationToken);
            if (exitCode != 0)
            {
                var actualError = string.Join("\n", (error ?? string.Empty)
                    .Split('\n')
                    .Where(line => !line.Contains("Using a password on the command line", StringComparison.OrdinalIgnoreCase)));
                _logger.LogWarning("Realmlist update for stack {StackId} exited {Exit}: {Error}", stack.Id, exitCode, actualError);
            }
            else
            {
                _logger.LogInformation("Realmlist for stack {StackId} set to {Host}:{Port} ({Realm}).",
                    stack.Id, realmAddress, stack.WorldServerPort, realmName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update acore_auth.realmlist for stack {StackId}.", stack.Id);
        }
    }

    private static string EscapeSqlLiteral(string value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "''");

    /// <summary>Runs a raw <c>docker</c> CLI invocation and returns (exitCode, stdout, stderr).</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerCliAsync(string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private async Task<StackDetailsDto> MapAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        // Get cached update status
        var outdatedModules = string.IsNullOrEmpty(stack.OutdatedModulesJson)
            ? new List<ModuleVersionStatusDto>()
            : Deserialize<List<ModuleVersionStatusDto>>(stack.OutdatedModulesJson) ?? new List<ModuleVersionStatusDto>();

        // Get cached CI build status if available
        CiBuildStatusDto? ciBuildStatus = null;
        if (!string.IsNullOrEmpty(stack.LatestCoreBuildStatus))
        {
            var cachedChecks = string.IsNullOrEmpty(stack.LatestCoreBuildChecksJson)
                ? new List<CiCheckDto>()
                : Deserialize<List<CiCheckDto>>(stack.LatestCoreBuildChecksJson) ?? new List<CiCheckDto>();
            
            ciBuildStatus = new CiBuildStatusDto
            {
                Status = stack.LatestCoreBuildStatus,
                CriticalChecks = cachedChecks,
                CheckedAt = stack.LatestCoreBuildStatusCheckedAt ?? DateTime.UtcNow,
                TotalChecks = cachedChecks.Count,
                PassedChecks = cachedChecks.Count(c => c.Conclusion == "success"),
                FailedChecks = cachedChecks.Count(c => c.Conclusion == "failure" || c.Conclusion == "timed_out" || c.Conclusion == "action_required")
            };
        }

        // Deployment drift: a built stack whose generated runtime artifacts predate the current template
        // version should be re-applied. Never-built stacks have no artifacts yet, so they aren't flagged.
        var isBuilt = !string.IsNullOrEmpty(stack.CoreCommitSha);
        var isRuntimeConfigOutdated = isBuilt && stack.RuntimeArtifactVersion < RuntimeArtifactTemplate.CurrentVersion;

        var updateStatus = new StackUpdateStatusDto
        {
            StackId = stack.Id,
            HasUpdates = stack.IsOutdated,
            IsCoreOutdated = stack.IsCoreOutdated,
            OutdatedModuleCount = stack.OutdatedModuleCount,
            CurrentCoreSha = stack.CoreCommitSha,
            LatestCoreSha = stack.LatestAvailableCoreSha,
            OutdatedModules = outdatedModules,
            LastCheckedAt = stack.LastUpdateCheckAt,
            LatestCoreBuildStatus = ciBuildStatus,
            IsRuntimeConfigOutdated = isRuntimeConfigOutdated,
            RuntimeArtifactVersion = stack.RuntimeArtifactVersion,
            RequiredRuntimeArtifactVersion = RuntimeArtifactTemplate.CurrentVersion
        };

        // Get containers and determine actual runtime status
        var containers = await GetContainersAsync(stack.Id, cancellationToken);
        var services = BuildServiceList(containers);
        await EnrichArmoryHealthAsync(stack.Id, services, containers, cancellationToken);
        containers = ApplyServiceHealthToContainers(containers, services);
        var runtimeStatus = DetermineRuntimeStatus(stack.Status, containers);

        // While a detached start/restart/start-database job is running, report Starting so both the list
        // and detail views reflect the in-progress operation (containers aren't up yet, so the raw
        // runtime status would otherwise read Stopped) and the Start button stays hidden/disabled.
        var job = _stackJobService.GetStatus(stack.Id);
        if (job is { IsRunning: true } && job.Action is not StackJobAction.Stop
            && runtimeStatus is not (StackStatus.Running or StackStatus.Building or StackStatus.Initializing))
        {
            runtimeStatus = StackStatus.Starting;
        }

        var externalReconnect = EvaluateExternalReconnect(stack);

        return new StackDetailsDto
        {
            StackId = stack.Id,
            StackName = stack.StackName,
            ServerType = stack.ServerType,
            Status = runtimeStatus,
            CreatedAt = stack.CreatedAt,
            Containers = containers,
            Services = services,
            Configuration = new StackConfigurationDto
            {
                StackName = stack.StackName,
                ServerType = stack.ServerType,
                ModuleIds = Deserialize<List<string>>(stack.ModuleIdsJson) ?? [],
                Database = new DatabaseConfigDto
                {
                    // Secrets are not returned in the standard payload; use the audited reveal endpoint
                    // (GET /api/stacks/{id}/database-credentials). Blank here means "unchanged" on update.
                    RootPassword = string.Empty,
                    Port = stack.DatabasePort
                },
                Ports = new PortConfigDto
                {
                    AuthServer = stack.AuthServerPort,
                    WorldServer = stack.WorldServerPort,
                    SoapPort = stack.SoapPort
                },
                Advanced = new AdvancedConfigDto
                {
                    MaxPlayers = stack.MaxPlayers,
                    RealmName = stack.RealmName,
                    RealmlistHost = stack.RealmlistHostOverride,
                    ServiceEnvVars = BuildServiceEnvDto(stack),
                    // Back-compat mirror of the worldserver bucket for legacy readers.
                    CustomEnvVars = Deserialize<Dictionary<string, string>>(stack.CustomEnvVarsJson) ?? new Dictionary<string, string>()
                },
                Deployment = new DeploymentConfigDto
                {
                    Target = stack.DeploymentTarget,
                    ExternalHost = stack.ExternalHost,
                    ExternalSshPort = stack.ExternalSshPort == 0 ? 22 : stack.ExternalSshPort,
                    ExternalSshUser = stack.ExternalSshUser,
                    // Never return the private key material to clients.
                    ExternalSshPrivateKey = string.Empty
                },
                // Surface the user-supplied fork for custom-repository server types so the UI can show it.
                CustomFork = _serverTypeCatalog.AllowsCustomRepository(stack.ServerType)
                    ? new CustomForkConfigDto
                    {
                        RepositoryUrl = stack.CoreRepositoryUrl,
                        Branch = stack.CoreBranch
                    }
                    : null,
                ArmoryAccounts = MapArmoryAccountsConfig(stack)
            },
            UpdateStatus = updateStatus,
            IsAdminAccountInitialized = stack.IsAdminAccountInitialized,
            AdminAccountInitializedAt = stack.AdminAccountInitializedAt,
            ArmoryPort = stack.ArmoryPort,
            ArmoryRunning = containers.Any(c =>
                c.Name.Contains("armory", StringComparison.OrdinalIgnoreCase)
                && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase)),
            ModulesPendingRebuild = GetModulesPendingRebuild(stack.Id, Deserialize<List<string>>(stack.ModuleIdsJson) ?? []),
            NeedsExternalReconnect = externalReconnect.NeedsReconnect,
            ExternalReconnectReason = externalReconnect.Reason,
            HasCompletedBuild = stack.LastBuiltAt.HasValue || !string.IsNullOrEmpty(stack.CoreCommitSha),
        };
    }

    private (bool NeedsReconnect, string? Reason) EvaluateExternalReconnect(ManagedStackEntity stack)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            return (false, null);
        }

        if (string.IsNullOrWhiteSpace(stack.ExternalSshPrivateKey))
        {
            return (true, "SSH credentials are missing. Re-enter the remote engine connection details.");
        }

        try
        {
            _secretProtector.Unprotect(stack.ExternalSshPrivateKey);
        }
        catch (CryptographicException)
        {
            return (true,
                "The manager encryption key was lost (often after pruning the data volume). Re-enter the SSH private key to reconnect this external stack.");
        }

        return (false, null);
    }

    /// <summary>
    /// Module IDs selected on the stack but missing from the build checkout (not yet cloned/compiled).
    /// </summary>
    private List<string> GetModulesPendingRebuild(string stackId, List<string> moduleIds)
    {
        if (moduleIds.Count == 0)
        {
            return [];
        }

        var modulesPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk", "modules");
        if (!Directory.Exists(modulesPath))
        {
            return moduleIds;
        }

        return moduleIds
            .Where(id => !Directory.Exists(Path.Combine(modulesPath, id)))
            .ToList();
    }

    private async Task<List<ContainerStatusDto>> GetContainersAsync(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            var dockerContext = await ResolveDockerContextAsync(stackId, cancellationToken);
            var containers = await _dockerService.ListContainersAsync(
                DockerComposeOverrideGenerator.GetComposeProjectName(stackId), dockerContext, cancellationToken);
            return containers.ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// The stack's canonical, always-shown services in display order. Every stack can run these, so
    /// they are surfaced even when stopped/absent to keep per-service controls available.
    /// </summary>
    private static readonly (string Service, string DisplayName, string Category)[] CanonicalServices =
    {
        ("ac-database", "Database", "core"),
        ("ac-authserver", "Auth Server", "core"),
        ("ac-worldserver", "World Server", "core"),
        ("frontend-armory", "Armory", "armory"),
        ("client", "Client Files", "client"),
    };

    /// <summary>Display metadata for the non-canonical (init/utility) services we know about.</summary>
    private static readonly Dictionary<string, (string DisplayName, string Category)> ExtraServiceMeta = new(StringComparer.OrdinalIgnoreCase)
    {
        ["armory-assets"] = ("Armory Assets", "armory"),
        ["ac-db-import"] = ("DB Import", "init"),
        ["ac-client-data-init"] = ("Client Data Init", "init"),
        ["ac-tools"] = ("Tools", "utility"),
        ["ac-dev-server"] = ("Dev Server", "utility"),
    };

    /// <summary>Service names that may be targeted by a per-service lifecycle action (allow-list).</summary>
    private static readonly HashSet<string> ControllableServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "ac-database", "ac-authserver", "ac-worldserver", "frontend-armory", "client",
        "ac-db-import", "ac-client-data-init", "ac-tools", "ac-dev-server",
    };

    /// <summary>
    /// Merges the canonical service set with the live container list so the UI always sees the core
    /// services (with an <c>absent</c> state when not created) plus any other containers that exist.
    /// </summary>
    private static List<StackServiceDto> BuildServiceList(List<ContainerStatusDto> containers)
    {
        var byService = new Dictionary<string, ContainerStatusDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers)
        {
            var service = !string.IsNullOrEmpty(container.Service) ? container.Service : GuessServiceFromName(container.Name);
            if (!string.IsNullOrEmpty(service))
            {
                byService[service] = container;
            }
        }

        var result = new List<StackServiceDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (service, displayName, category) in CanonicalServices)
        {
            byService.TryGetValue(service, out var container);
            result.Add(ToServiceDto(service, displayName, category, container));
            seen.Add(service);
        }

        // Append any other existing containers (init/utility or unknown) so nothing is hidden.
        foreach (var container in containers)
        {
            var service = !string.IsNullOrEmpty(container.Service) ? container.Service : GuessServiceFromName(container.Name);
            if (string.IsNullOrEmpty(service) || !seen.Add(service))
            {
                continue;
            }

            var (displayName, category) = ExtraServiceMeta.TryGetValue(service, out var meta)
                ? meta
                : (Humanize(service), "utility");
            result.Add(ToServiceDto(service, displayName, category, container));
        }

        return result;
    }

    private static StackServiceDto ToServiceDto(string service, string displayName, string category, ContainerStatusDto? container)
    {
        if (container is null)
        {
            return new StackServiceDto { Service = service, DisplayName = displayName, Category = category, State = "absent" };
        }

        return new StackServiceDto
        {
            Service = service,
            DisplayName = displayName,
            Category = category,
            ContainerName = container.Name,
            State = string.IsNullOrWhiteSpace(container.Status) ? "unknown" : container.Status.ToLowerInvariant(),
            Health = ResolveServiceHealth(service, container),
            StartedAt = container.StartedAt == DateTime.UnixEpoch ? null : container.StartedAt,
        };
    }

    private static string ResolveServiceHealth(string service, ContainerStatusDto container)
    {
        if (!string.Equals(container.Health, "unknown", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(container.Health))
        {
            return container.Health;
        }

        if (IsContainerRunning(container)
            && (service.Equals("ac-authserver", StringComparison.OrdinalIgnoreCase)
                || service.Equals("ac-worldserver", StringComparison.OrdinalIgnoreCase)))
        {
            return "healthy";
        }

        return string.IsNullOrWhiteSpace(container.Health) ? "unknown" : container.Health;
    }

    private static bool IsContainerRunning(ContainerStatusDto container) =>
        container.Status.Contains("running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// When Docker has no healthcheck for the armory, probe its in-container /health endpoint so the
    /// overview can show a real status instead of "unknown".
    /// </summary>
    private async Task EnrichArmoryHealthAsync(
        string stackId,
        List<StackServiceDto> services,
        List<ContainerStatusDto> containers,
        CancellationToken cancellationToken)
    {
        var armoryService = services.FirstOrDefault(s =>
            s.Service.Equals("frontend-armory", StringComparison.OrdinalIgnoreCase));
        if (armoryService is null
            || !IsServiceRunning(armoryService)
            || !string.Equals(armoryService.Health, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var armoryContainer = containers.FirstOrDefault(c =>
            (!string.IsNullOrEmpty(c.Service)
                && c.Service.Equals("frontend-armory", StringComparison.OrdinalIgnoreCase))
            || GuessServiceFromName(c.Name).Equals("frontend-armory", StringComparison.OrdinalIgnoreCase));
        if (armoryContainer is null || !IsContainerRunning(armoryContainer))
        {
            return;
        }

        try
        {
            var dockerContext = await ResolveDockerContextAsync(stackId, cancellationToken);
            var contextArg = string.IsNullOrWhiteSpace(dockerContext) ? string.Empty : $"--context {dockerContext} ";
            var arguments =
                $"{contextArg}exec {armoryContainer.Name} node -e \"fetch('http://127.0.0.1:48733/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))\"";
            var (exitCode, _, _) = await RunDockerCliAsync(arguments, cancellationToken);
            armoryService.Health = exitCode == 0 ? "healthy" : "unhealthy";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Armory health probe failed for stack {StackId}.", stackId);
            armoryService.Health = "unhealthy";
        }
    }

    private static bool IsServiceRunning(StackServiceDto service) =>
        service.State.Contains("running", StringComparison.OrdinalIgnoreCase);

    private static List<ContainerStatusDto> ApplyServiceHealthToContainers(
        List<ContainerStatusDto> containers,
        List<StackServiceDto> services)
    {
        var healthByService = services.ToDictionary(
            s => s.Service,
            s => s.Health,
            StringComparer.OrdinalIgnoreCase);

        return containers.Select(container =>
        {
            var service = !string.IsNullOrEmpty(container.Service)
                ? container.Service
                : GuessServiceFromName(container.Name);
            if (string.IsNullOrEmpty(service)
                || !healthByService.TryGetValue(service, out var health)
                || string.IsNullOrWhiteSpace(health))
            {
                return container;
            }

            return new ContainerStatusDto
            {
                ContainerId = container.ContainerId,
                Name = container.Name,
                Service = container.Service,
                Status = container.Status,
                Health = health,
                StartedAt = container.StartedAt,
            };
        }).ToList();
    }

    /// <summary>Fallback service mapping from a container name suffix when the compose label is absent.</summary>
    private static string GuessServiceFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) { return string.Empty; }
        if (name.EndsWith("-database", StringComparison.OrdinalIgnoreCase)) { return "ac-database"; }
        if (name.EndsWith("-authserver", StringComparison.OrdinalIgnoreCase)) { return "ac-authserver"; }
        if (name.EndsWith("-worldserver", StringComparison.OrdinalIgnoreCase)) { return "ac-worldserver"; }
        if (name.Contains("-armory-assets", StringComparison.OrdinalIgnoreCase)) { return "armory-assets"; }
        if (name.Contains("-armory", StringComparison.OrdinalIgnoreCase)) { return "frontend-armory"; }
        if (name.EndsWith("-client", StringComparison.OrdinalIgnoreCase)) { return "client"; }
        if (name.EndsWith("-db-import", StringComparison.OrdinalIgnoreCase)) { return "ac-db-import"; }
        if (name.EndsWith("-client-data-init", StringComparison.OrdinalIgnoreCase)) { return "ac-client-data-init"; }
        if (name.EndsWith("-tools", StringComparison.OrdinalIgnoreCase)) { return "ac-tools"; }
        if (name.EndsWith("-dev-server", StringComparison.OrdinalIgnoreCase)) { return "ac-dev-server"; }
        return string.Empty;
    }

    /// <summary>Turns a service id like <c>ac-foo-bar</c> into a friendly <c>Foo Bar</c> label.</summary>
    private static string Humanize(string service)
    {
        var trimmed = service.StartsWith("ac-", StringComparison.OrdinalIgnoreCase) ? service[3..] : service;
        var words = trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }

    public async Task<bool> ServiceActionAsync(string stackId, string service, StackServiceAction action, CancellationToken cancellationToken = default)
    {
        if (!ControllableServices.Contains(service))
        {
            throw new InvalidOperationException($"Unknown or unmanaged service '{service}'.");
        }

        // The armory has bespoke start/stop logic (image build + DB dependency + override toggling),
        // so route its actions through the dedicated helpers rather than raw compose commands.
        if (string.Equals(service, "frontend-armory", StringComparison.OrdinalIgnoreCase))
        {
            if (action == StackServiceAction.Stop)
            {
                return await StopArmoryAsync(stackId, cancellationToken);
            }

            if (action == StackServiceAction.Restart)
            {
                // Detach from the request token so a page refresh can't interrupt the restart
                // (see StartArmoryInternalAsync/StopArmoryAsync for the rationale).
                var armoryToken = CancellationToken.None;

                var armoryStack = await _dbContext.ManagedStacks
                    .SingleOrDefaultAsync(item => item.Id == stackId, armoryToken);
                if (armoryStack is null) { return false; }

                var armoryRepo = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
                if (!Directory.Exists(armoryRepo))
                {
                    throw new InvalidOperationException("Stack has not been built yet.");
                }

                await EnsureRuntimeConfigurationAsync(armoryStack, armoryRepo, armoryToken, includeArmory: true);
                await RunDockerComposeAsync(stackId, "restart frontend-armory", armoryRepo, armoryToken);
                return true;
            }

            // Start or Recreate: (re)build image, ensure DB, bring the armory up. Recreate forces a
            // fresh container so config/env changes are actually applied. Runs detached from the
            // request token (see StartArmoryInternalAsync).
            return await StartArmoryInternalAsync(stackId, action == StackServiceAction.Recreate);
        }

        // The client-server runs the shared azeroth-platform-client image built from the manager's baked
        // source. "Rebuild & Restart" must rebuild that image so ClientServer code changes are actually
        // picked up — a plain force-recreate would reuse the cached image (mirrors the armory's
        // rebuild-on-recreate). Runs detached from the request token so a page refresh can't interrupt it.
        if (string.Equals(service, "client", StringComparison.OrdinalIgnoreCase)
            && action == StackServiceAction.Recreate)
        {
            var clientToken = CancellationToken.None;
            var clientStack = await _dbContext.ManagedStacks
                .SingleOrDefaultAsync(item => item.Id == stackId, clientToken);
            if (clientStack is null) { return false; }

            EnsureStackLifecycleAllowed(clientStack, "control a service of");

            var clientRepo = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
            if (!Directory.Exists(clientRepo))
            {
                throw new InvalidOperationException("Stack has not been built yet.");
            }

            await _clientServerImageService.RebuildImageAsync(dockerContext: null, clientToken);

            await EnsureRuntimeConfigurationAsync(clientStack, clientRepo, clientToken, includeClient: true);
            await ShipExternalStackImagesAsync(clientStack, includeArmory: false, includeClient: true, clientToken);
            await PrepareFixedNameServiceRecreateAsync(stackId, clientStack, "client", clientRepo, clientToken);
            await RunDockerComposeAsync(stackId, "up -d --force-recreate --no-deps client", clientRepo, clientToken);

            // A recreated client container starts with only its env fallback portal; re-push the
            // registry (which also refreshes each stack's branding + news) so it self-heals immediately.
            await RepushRegistrySafeAsync(clientToken);
            return true;
        }

        // Recreating the database would tear down and rebuild the container. Its data lives in a
        // named volume so it would survive, but the operation is needless downtime and risky, so we
        // refuse it outright (Restart covers the legitimate "bounce the DB" case). Auth and world
        // servers are long-running game processes with live state; force-recreate is similarly risky
        // and unnecessary when Restart reapplies the current container.
        if (action == StackServiceAction.Recreate
            && (string.Equals(service, "ac-database", StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, "ac-authserver", StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, "ac-worldserver", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Rebuild & Restart is disabled for the database, auth server, and world server. Use Restart instead.");
        }

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return false;
        }

        EnsureStackLifecycleAllowed(stack, "control a service of");

        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        if (!Directory.Exists(repoPath))
        {
            throw new InvalidOperationException("Stack has not been built yet.");
        }

        switch (action)
        {
            case StackServiceAction.Start:
            case StackServiceAction.Recreate:
                // Regenerate .env/override so compose has the ports and (if enabled) the armory
                // service available, then bring just this service up. `up -d` also starts any
                // compose depends_on (e.g. the database) so a single service can be started safely.
                await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken, includeArmory: stack.ArmoryEnabled);
                if (action == StackServiceAction.Recreate)
                {
                    await PrepareFixedNameServiceRecreateAsync(stackId, stack, service, repoPath, cancellationToken);
                }

                var recreate = action == StackServiceAction.Recreate ? " --force-recreate" : string.Empty;
                await RunDockerComposeAsync(stackId, $"up -d{recreate} {service}", repoPath, cancellationToken);
                break;

            case StackServiceAction.Stop:
                await RunDockerComposeAsync(stackId, $"stop {service}", repoPath, cancellationToken);
                break;

            case StackServiceAction.Restart:
                await RunDockerComposeAsync(stackId, $"restart {service}", repoPath, cancellationToken);
                break;
        }

        return true;
    }

    /// <summary>
    /// Determines the actual runtime status based on container states.
    /// Required containers: database, authserver, worldserver
    /// Init containers: db-import, client-data-init
    /// </summary>
    private static StackStatus DetermineRuntimeStatus(StackStatus dbStatus, List<ContainerStatusDto> containers)
    {
        // If currently building, don't override
        if (dbStatus == StackStatus.Building)
        {
            return StackStatus.Building;
        }

        // No containers means stack not deployed yet or all removed
        if (containers.Count == 0)
        {
            return StackStatus.Stopped;
        }

        // Check for first-time initialization containers (db-import, client-data-init)
        var initContainers = containers
            .Where(c => c.Name.Contains("db-import", StringComparison.OrdinalIgnoreCase) || 
                       c.Name.Contains("client-data-init", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var anyInitRunning = initContainers.Any(c => c.Status.Equals("running", StringComparison.OrdinalIgnoreCase));

        // If init containers are running, stack is initializing
        if (anyInitRunning)
        {
            return StackStatus.Initializing;
        }

        // Check required service containers
        var requiredContainers = containers
            .Where(c => RequiredRunningServiceNames.Any(service => c.Name.Contains(service, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (requiredContainers.Count == 0)
        {
            return StackStatus.Stopped;
        }

        // Group by service type to detect missing or stopped services
        var databaseRunning = requiredContainers.Any(c => 
            c.Name.Contains("database", StringComparison.OrdinalIgnoreCase) && 
            c.Status.Equals("running", StringComparison.OrdinalIgnoreCase));
        
        var authserverRunning = requiredContainers.Any(c => 
            c.Name.Contains("authserver", StringComparison.OrdinalIgnoreCase) && 
            c.Status.Equals("running", StringComparison.OrdinalIgnoreCase));
        
        var worldserverRunning = requiredContainers.Any(c => 
            c.Name.Contains("worldserver", StringComparison.OrdinalIgnoreCase) && 
            c.Status.Equals("running", StringComparison.OrdinalIgnoreCase));

        var runningCount = (databaseRunning ? 1 : 0) + (authserverRunning ? 1 : 0) + (worldserverRunning ? 1 : 0);

        // All 3 required services running = healthy
        if (runningCount == 3)
        {
            return StackStatus.Running;
        }

        // None running = stopped
        if (runningCount == 0)
        {
            return StackStatus.Stopped;
        }

        // Some running, some not = degraded (e.g., worldserver crash-looping)
        // This indicates a problem but stack is partially operational
        return StackStatus.Degraded;
    }

    private async Task EnsureRuntimeConfigurationAsync(
        ManagedStackEntity stack,
        string repoPath,
        CancellationToken cancellationToken,
        bool? includeArmory = null,
        bool? includeClient = null)
    {
        // The armory service is only rendered into the override when requested; otherwise a plain
        // `up -d` would try to use the (registry-less) armory image and fail the whole stack.
        var renderArmory = includeArmory ?? stack.ArmoryEnabled;
        var renderClient = includeClient ?? stack.ClientEnabled;
        var environmentPath = Path.Combine(repoPath, ".env");
        var overridePath = Path.Combine(repoPath, "docker-compose.override.yml");
        var composeProjectName = DockerComposeOverrideGenerator.GetComposeProjectName(stack.Id);

        // Older stacks (created before the armory feature) have no port assigned yet.
        if (stack.ArmoryPort == 0)
        {
            stack.ArmoryPort = await AllocateStackPortAsync(cancellationToken, StackNetworkDefaults.DefaultArmoryPort);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Older stacks (created before the client-server feature) have no client port assigned yet.
        if (stack.ClientPort == 0)
        {
            stack.ClientPort = await AllocateStackPortAsync(cancellationToken, StackNetworkDefaults.DefaultClientPort, stack.ArmoryPort);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Generate and persist a random armory session secret on first use (independent of the DB
        // password) so it can't be recomputed by anyone who only learns the DB credentials.
        if (renderArmory && string.IsNullOrEmpty(stack.ArmorySessionSecret))
        {
            stack.ArmorySessionSecret = GenerateArmorySessionSecret();
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Server config (etc) and logs are pre-seeded named volumes for every stack (local and external).
        // Docker treats the slash-less volume names as named-volume references, declared external in the
        // override and resolved to the volumes the manager seeds below.
        var logsPath = DockerComposeOverrideGenerator.LogsVolumeName(stack.Id);
        var etcPath = DockerComposeOverrideGenerator.EtcVolumeName(stack.Id);

        // Host-interface binding policy (see DockerOptions):
        //  - MySQL/SOAP are data-plane only → DataPlaneBindAddress (loopback by default).
        //  - auth/world are the game protocol the client dials directly → always all interfaces.
        //  - armory/client are player-facing HTTP → PublishBindAddress (loopback by default).
        // External stacks run on a remote engine: players and the manager reach them over the network, so
        // the game protocol and player-facing HTTP ports must stay on all interfaces. The data plane
        // (MySQL/SOAP) is only needed by the manager, so it can optionally be pinned to the remote's
        // private/VPC interface via ExternalDataPlaneBindAddress instead of being exposed on all interfaces.
        var localStack = stack.DeploymentTarget != DeploymentTarget.External;
        static string WithColon(string? configured, string fallback) =>
            (string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim()) + ":";

        // A per-stack override (set via the armory network settings) wins over the manager default so an
        // operator can expose the armory/client on LAN/VPC/all-interfaces without hand-editing this .env
        // (which is unreachable once the stack is pushed to a remote host). Blank falls back to the policy
        // default: loopback for local stacks, all interfaces for external ones.
        var publishBindIp = TryParseBindAddress(stack.PublishBindAddress);
        var publishBind = publishBindIp is not null
            ? publishBindIp + ":"
            : (localStack ? WithColon(_dockerOptions.PublishBindAddress, "127.0.0.1") : string.Empty);
        var externalDataBind = ResolveExternalDataPlaneBind(stack);
        var dataBind = localStack
            ? WithColon(_dockerOptions.DataPlaneBindAddress, "127.0.0.1")
            : (string.IsNullOrWhiteSpace(externalDataBind) ? string.Empty : externalDataBind + ":");
        var environment = new StringBuilder()
            .AppendLine("# AzerothCore Environment Configuration")
            .AppendLine($"DOCKER_DB_ROOT_PASSWORD=\"{stack.DatabaseRootPassword.Replace("$", "$$")}\"")
            .AppendLine($"DOCKER_DB_EXTERNAL_PORT={dataBind}{stack.DatabasePort}")
            .AppendLine($"DOCKER_WORLD_EXTERNAL_PORT={stack.WorldServerPort}")
            .AppendLine($"DOCKER_SOAP_EXTERNAL_PORT={dataBind}{stack.SoapPort}")
            .AppendLine($"DOCKER_AUTH_EXTERNAL_PORT={stack.AuthServerPort}")
            .AppendLine($"DOCKER_ARMORY_EXTERNAL_PORT={publishBind}{stack.ArmoryPort}")
            .AppendLine($"DOCKER_CLIENT_EXTERNAL_PORT={publishBind}{stack.ClientPort}")
            .AppendLine($"DOCKER_IMAGE_TAG={stack.Id}")
            .AppendLine($"COMPOSE_PROJECT_NAME={composeProjectName}")
            .AppendLine("DOCKER_USER_ID=1000")
            .AppendLine("DOCKER_GROUP_ID=1000")
            .AppendLine("DOCKER_USER=acore")
            .AppendLine($"DOCKER_VOL_LOGS={logsPath}")
            .AppendLine($"DOCKER_VOL_ETC={etcPath}");

        await File.WriteAllTextAsync(environmentPath, environment.ToString(), cancellationToken);
        await File.WriteAllTextAsync(
            overridePath,
            GenerateRuntimeDockerComposeOverride(stack, composeProjectName, repoPath, renderArmory, renderClient),
            cancellationToken);

        // Stamp the runtime-artifact template version now that fresh artifacts have been written. This is
        // how "deployment drift" is cleared: any stack generated by an older manager (or before this
        // tracking existed) carries a lower version and is surfaced as "re-apply required" until it is
        // regenerated here.
        if (stack.RuntimeArtifactVersion != RuntimeArtifactTemplate.CurrentVersion)
        {
            stack.RuntimeArtifactVersion = RuntimeArtifactTemplate.CurrentVersion;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Every stack references pre-seeded named volumes (no host bind mounts), so create + populate
        // those volumes on the stack's engine before compose brings the stack up.
        await SeedStackVolumesAsync(stack, repoPath, cancellationToken);
    }

    /// <summary>
    /// Creates and populates a stack's named volumes (modules, lua, etc, logs, client base/overlay/cache,
    /// armory assets) from the manager's local build directory. Runs against the stack's engine (the
    /// local daemon, or the remote engine for external stacks). Best-effort per volume so a transient
    /// hiccup on one does not abort the whole start.
    /// </summary>
    private async Task SeedStackVolumesAsync(ManagedStackEntity stack, string repoPath, CancellationToken cancellationToken)
    {
        var modulesDir = Path.Combine(repoPath, "modules");
        if (Directory.Exists(modulesDir))
        {
            await _remoteEngine.SeedVolumeAsync(stack, DockerComposeOverrideGenerator.ModulesVolumeName(stack.Id), modulesDir, cancellationToken);
        }

        // Server config (etc): seed whatever the manager holds locally (operator edits + materialized
        // module confs). On a fresh stack this dir is empty and the container populates the volume from
        // the image on first start; the config editor fetches it back on demand (see ServerConfigService).
        var etcDir = Path.Combine(repoPath, "env", "dist", "etc");
        Directory.CreateDirectory(etcDir);
        var etcVolume = DockerComposeOverrideGenerator.EtcVolumeName(stack.Id);
        await _remoteEngine.SeedVolumeAsync(stack, etcVolume, etcDir, cancellationToken);

        // Logs starts empty but the named volume must exist for the (external:true) declaration to resolve.
        var logsDir = Path.Combine(repoPath, "env", "dist", "logs");
        Directory.CreateDirectory(logsDir);
        var logsVolume = DockerComposeOverrideGenerator.LogsVolumeName(stack.Id);
        await _remoteEngine.SeedVolumeAsync(stack, logsVolume, logsDir, cancellationToken);

        // Docker creates named volumes root-owned, but the AzerothCore services run as uid/gid 1000
        // (DOCKER_USER_ID/DOCKER_GROUP_ID) and must write their configs + logs. Chown these volumes so
        // db-import/worldserver/authserver can seed .conf files and write logs (otherwise db-import exits
        // with a permission error and the stack is stuck "Starting").
        await _remoteEngine.SetVolumeOwnershipAsync(stack, etcVolume, AcoreServiceUid, AcoreServiceGid, cancellationToken);
        await _remoteEngine.SetVolumeOwnershipAsync(stack, logsVolume, AcoreServiceUid, AcoreServiceGid, cancellationToken);

        // Armory 3D model-viewer dataset lives in the stack's armory-assets volume (uploaded via Armory tab).
        if (stack.ArmoryEnabled)
        {
            var assetsVolume = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stack.Id);
            if (!await _remoteEngine.VolumeExistsAsync(stack, assetsVolume, cancellationToken))
            {
                await _remoteEngine.EnsureVolumeExistsAsync(stack, assetsVolume, cancellationToken);
                await _remoteEngine.SetVolumeWorldReadableAsync(stack, assetsVolume, cancellationToken);
            }

            var staticVolume = DockerComposeOverrideGenerator.ArmoryStaticVolumeName(stack.Id);
            if (!await _remoteEngine.VolumeExistsAsync(stack, staticVolume, cancellationToken))
            {
                await _remoteEngine.EnsureVolumeExistsAsync(stack, staticVolume, cancellationToken);
            }
        }

        var stackRoot = Path.GetDirectoryName(repoPath.TrimEnd(Path.DirectorySeparatorChar)) ?? repoPath;
        var luaDir = Migrations.MigrationLayout.LuaScriptsDir(stackRoot);
        Directory.CreateDirectory(luaDir);
        if (DirectoryHasLuaScripts(luaDir))
        {
            await _remoteEngine.SeedVolumeAsync(stack, DockerComposeOverrideGenerator.LuaVolumeName(stack.Id), luaDir, cancellationToken);
        }

        // Client volumes: the shared base (seeded once per host — skip if already present since it's
        // ~17 GB), plus this stack's overlay + cache (created + seeded even when empty so the
        // external:true declarations resolve).
        if (stack.ClientEnabled)
        {
            // Per-stack base client (uploaded on the stack's Client tab). Seed it if the stack has a base
            // uploaded and its volume is not yet populated (skip re-seeding the ~17 GB base on every start;
            // uploads re-seed the volume directly).
            var baseVolume = DockerComposeOverrideGenerator.ClientBaseVolumeName(stack.Id);
            if (!await _remoteEngine.VolumeExistsAsync(stack, baseVolume, cancellationToken))
            {
                await _remoteEngine.EnsureVolumeExistsAsync(stack, baseVolume, cancellationToken);
            }

            var overlayDir = ClientOverlayDir(stackRoot);
            var cacheDir = ClientCacheDir(stackRoot);
            Directory.CreateDirectory(overlayDir);
            Directory.CreateDirectory(cacheDir);
            await _remoteEngine.SeedVolumeAsync(
                stack, DockerComposeOverrideGenerator.ClientOverlayVolumeName(stack.Id), overlayDir, cancellationToken);
            await _remoteEngine.SeedVolumeAsync(
                stack, DockerComposeOverrideGenerator.ClientCacheVolumeName(stack.Id), cacheDir, cancellationToken);

            // Launcher distribution volume: created (empty) so the external:true declaration resolves even
            // before a launcher build targets this stack. LauncherBuildService re-seeds it with the exe +
            // build.json; never overwrite an existing populated volume here.
            var launcherVolume = DockerComposeOverrideGenerator.ClientLauncherDistVolumeName(stack.Id);
            if (!await _remoteEngine.VolumeExistsAsync(stack, launcherVolume, cancellationToken))
            {
                var launcherDir = Path.Combine(cacheDir, "..", "launcher-dist");
                Directory.CreateDirectory(launcherDir);
                await _remoteEngine.SeedVolumeAsync(stack, launcherVolume, launcherDir, cancellationToken);
            }
        }
    }

    private string GenerateRuntimeDockerComposeOverride(ManagedStackEntity stack, string composeProjectName, string repoPath, bool includeArmory, bool includeClient)
    {
        var serviceEnvironment = BuildServiceEnvironment(stack);

        // Lua scripts live under the stack root (parent of the cloned repo). Ensure the directory
        // exists so it can be seeded into the lua_scripts volume; mount it only when scripts are present.
        var stackRoot = Path.GetDirectoryName(repoPath.TrimEnd(Path.DirectorySeparatorChar)) ?? repoPath;
        var luaDir = Migrations.MigrationLayout.LuaScriptsDir(stackRoot);
        Directory.CreateDirectory(luaDir);
        var includeLua = DirectoryHasLuaScripts(luaDir);

        return DockerComposeOverrideGenerator.Generate(
            stack.Id,
            stack.StackName,
            serviceEnvironment,
            includeLua,
            includeArmory ? BuildArmoryComposeOptions(stack) : null,
            external: stack.DeploymentTarget == DeploymentTarget.External,
            client: includeClient ? BuildClientComposeOptions(stack) : null);
    }

    /// <summary>
    /// Builds the per-service env map the override generator consumes from the stack's persisted
    /// <see cref="ManagedStackEntity.ServiceEnvVarsJson"/>. Legacy flat vars (<c>CustomEnvVarsJson</c>,
    /// still written by stack discovery) are folded into the worldserver bucket when it has none, so
    /// pre-existing stacks keep applying their worldserver overrides after the migration.
    /// </summary>
    /// <summary>
    /// Normalizes the incoming advanced config into the two persisted JSON blobs: the per-service map
    /// (<c>ServiceEnvVarsJson</c>) and the legacy worldserver mirror (<c>CustomEnvVarsJson</c>). Legacy
    /// flat <see cref="AdvancedConfigDto.CustomEnvVars"/> seeds the worldserver bucket when the caller
    /// only sent flat vars, so old clients keep working.
    /// </summary>
    private static (string ServiceEnvJson, string WorldserverEnvJson) BuildEnvJson(AdvancedConfigDto advanced)
    {
        var perService = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (serviceId, bucket) in advanced.ServiceEnvVars ?? new())
        {
            if (string.IsNullOrWhiteSpace(serviceId))
            {
                continue;
            }

            perService[serviceId] = bucket is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(bucket);
        }

        var legacy = advanced.CustomEnvVars ?? new Dictionary<string, string>();
        if (legacy.Count > 0
            && (!perService.TryGetValue(ServiceEnvTemplateService.Worldserver, out var world) || world.Count == 0))
        {
            perService[ServiceEnvTemplateService.Worldserver] = new Dictionary<string, string>(legacy);
        }

        perService.TryGetValue(ServiceEnvTemplateService.Worldserver, out var worldserverBucket);
        var worldserverJson = JsonSerializer.Serialize(
            worldserverBucket ?? new Dictionary<string, string>(), JsonOptions);
        var serviceJson = JsonSerializer.Serialize(perService, JsonOptions);
        return (serviceJson, worldserverJson);
    }

    /// <summary>Reads the persisted per-service env map for the config DTO, folding legacy flat vars into worldserver.</summary>
    private Dictionary<string, Dictionary<string, string>> BuildServiceEnvDto(ManagedStackEntity stack)
    {
        var perService = Deserialize<Dictionary<string, Dictionary<string, string>>>(stack.ServiceEnvVarsJson)
            ?? new Dictionary<string, Dictionary<string, string>>();

        var legacy = Deserialize<Dictionary<string, string>>(stack.CustomEnvVarsJson)
            ?? new Dictionary<string, string>();

        if (legacy.Count > 0
            && (!perService.TryGetValue(ServiceEnvTemplateService.Worldserver, out var world) || world is null || world.Count == 0))
        {
            perService[ServiceEnvTemplateService.Worldserver] = legacy;
        }

        return perService;
    }

    private Dictionary<string, IReadOnlyDictionary<string, string>> BuildServiceEnvironment(ManagedStackEntity stack)
    {
        var perService = Deserialize<Dictionary<string, Dictionary<string, string>>>(stack.ServiceEnvVarsJson)
            ?? new Dictionary<string, Dictionary<string, string>>();

        var legacy = Deserialize<Dictionary<string, string>>(stack.CustomEnvVarsJson)
            ?? new Dictionary<string, string>();

        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (serviceId, bucket) in perService)
        {
            result[serviceId] = bucket ?? new Dictionary<string, string>();
        }

        if (legacy.Count > 0
            && (!result.TryGetValue(ServiceEnvTemplateService.Worldserver, out var world) || world.Count == 0))
        {
            result[ServiceEnvTemplateService.Worldserver] = legacy;
        }

        return result;
    }

    /// <summary>Per-stack client overlay directory (published patch MPQs) on the manager host.</summary>
    private static string ClientOverlayDir(string stackRoot) => Path.Combine(stackRoot, "client", "overlay");

    /// <summary>Per-stack client cache directory (hash cache + manifest snapshot) on the manager host.</summary>
    private static string ClientCacheDir(string stackRoot) => Path.Combine(stackRoot, "client", "cache");

    private static bool DirectoryHasLuaScripts(string luaDir)
        => Directory.Exists(luaDir) && Directory.EnumerateFileSystemEntries(luaDir).Any();

    /// <summary>
    /// Builds the client-server compose options. The mounts are always pre-seeded named volumes (shared
    /// base + per-stack overlay/cache) for both local and external stacks.
    /// </summary>
    private ClientComposeOptions BuildClientComposeOptions(ManagedStackEntity stack)
    {
        var managedPrefixes = string.Join(',', _clientOptions.ManagedPrefixes);
        var authToken = string.IsNullOrEmpty(stack.ArmorySessionSecret)
            ? GenerateArmorySessionSecret()
            : stack.ArmorySessionSecret;

        var displayName = !string.IsNullOrWhiteSpace(stack.LauncherDisplayName) ? stack.LauncherDisplayName
            : !string.IsNullOrWhiteSpace(stack.RealmName) ? stack.RealmName
            : stack.StackName;

        return new ClientComposeOptions
        {
            ImageName = _clientServerOptions.ImageName,
            ContainerPort = _clientServerOptions.ContainerPort,
            ManagedPrefixes = managedPrefixes,
            AuthToken = authToken,
            ManifestPrivateKey = _manifestSigningKeys.PrivateKeyPkcs8Base64,
            // The container verifies player logins against this stack's auth DB (reached over the host's
            // published DB port, same channel the armory uses), so a VPC/external stack needs no manager.
            LoginEnabled = true,
            RequireLogin = true,
            DbHost = "host.docker.internal",
            DbPort = stack.DatabasePort,
            DbUser = "root",
            DbPassword = stack.DatabaseRootPassword,
            // Portal fallback identity (used until the manager pushes the full replicated registry).
            StackId = stack.Id,
            AppName = string.IsNullOrWhiteSpace(displayName) ? "Azeroth Platform" : displayName,
            DisplayName = displayName,
            RealmlistHost = ResolveRealmlistHost(stack),
            RealmlistPort = stack.AuthServerPort,
            ArmoryPort = stack.ArmoryEnabled ? stack.ArmoryPort : 0,
            Template = stack.LauncherTemplate,
        };
    }

    private ArmoryComposeOptions BuildArmoryComposeOptions(ManagedStackEntity stack)
    {
        var realmName = string.IsNullOrWhiteSpace(stack.RealmName) ? stack.StackName : stack.RealmName;
        var (assetProxyUrl, assetsAvailable) = ResolveArmoryAssets(stack);
        return new ArmoryComposeOptions
        {
            ImageName = _armoryImageService.ImageNameFor(stack.Id),
            WebsiteName = string.IsNullOrWhiteSpace(realmName) ? "Armory" : $"{realmName}",
            RealmName = string.IsNullOrWhiteSpace(realmName) ? "AzerothCore" : realmName,
            RealmId = 1,
            // The armory reaches the stack's MySQL over the host's published DB port.
            DbHost = "host.docker.internal",
            DbPort = stack.DatabasePort,
            DbUser = "root",
            DbPassword = stack.DatabaseRootPassword,
            PlatformApiUrl = _armoryOptions.PlatformApiUrl,
            PlatformPublicUrl = _armoryOptions.PublicUrl,
            StackId = stack.Id,
            // When this stack has its own client container, the armory serves the launcher exe straight
            // from it (reached by service name on the compose network), never from the manager.
            ClientPortalUrl = stack.ClientEnabled
                ? $"http://{DockerComposeOverrideGenerator.ClientService}:{_clientServerOptions.ContainerPort}"
                : string.Empty,
            // Populated by EnsureRuntimeConfigurationAsync before the override is rendered; fall back
            // to a fresh secret defensively so a null is never written into the compose file.
            SessionSecret = string.IsNullOrEmpty(stack.ArmorySessionSecret)
                ? GenerateArmorySessionSecret()
                : stack.ArmorySessionSecret,
            // The armory proxies its /data/* model-viewer routes to a per-stack armory-assets sidecar
            // that serves the shared dataset from a pre-seeded named volume. Both are blank/false when no
            // dataset exists, so the sidecar is omitted and the armory runs with the model viewer disabled.
            AssetProxyUrl = assetProxyUrl,
            AssetsAvailable = assetsAvailable,
            EmailConfirmationEnabled = stack.ArmoryUseEmailConfirmation,
            EmailConfigured = stack.ArmoryEmailConfigured,
            Email = MapArmoryEmailForCompose(stack),
        };
    }

    private ArmoryEmailComposeOptions? MapArmoryEmailForCompose(ManagedStackEntity stack)
    {
        if (!stack.ArmoryUseEmailConfirmation || !stack.ArmoryEmailConfigured)
        {
            return null;
        }

        var email = ArmoryEmailConfigDefaults.DeserializeEmailConfig(stack.ArmoryEmailConfigJson);
        if (email is null)
        {
            return new ArmoryEmailComposeOptions();
        }

        var smtpPassword = string.IsNullOrWhiteSpace(stack.ArmoryEmailSmtpPasswordProtected)
            ? string.Empty
            : _secretProtector.Unprotect(stack.ArmoryEmailSmtpPasswordProtected);

        return new ArmoryEmailComposeOptions
        {
            SmtpHost = email.SmtpHost,
            SmtpPort = email.SmtpPort,
            SmtpSecurity = email.SmtpSecurity,
            SmtpUsername = email.SmtpUsername,
            SmtpPassword = smtpPassword,
            FromAddress = email.FromAddress,
            FromName = email.FromName,
            VerificationSubject = email.VerificationSubject,
            VerificationBodyHtml = email.VerificationBodyHtml,
        };
    }

    /// <summary>
    /// Resolves the model-viewer / progression asset wiring.
    /// <c>{ArmorySourcePath}/static/data</c> (or the stack's uploaded copy). Returns the proxy URL
    /// and availability when model-viewer metadata or progression artwork is present; otherwise the
    /// sidecar is omitted and the armory falls back to local/baked assets only.
    /// </summary>
    private (string AssetProxyUrl, bool AssetsAvailable) ResolveArmoryAssets(ManagedStackEntity stack)
    {
        if (!stack.ArmoryEnabled)
        {
            return (string.Empty, false);
        }

        // Volume-first: the dataset may exist only in the stack's armory-assets Docker volume.
        var uploaded = _armoryAssetsOptions.DataPathFor(stack.Id);
        if (HasArmoryAssetDataset(uploaded))
        {
            return (_armoryOptions.AssetProxyUrl, true);
        }

        var baked = Path.Combine(_armoryOptions.SourcePath, "static", "data");
        if (HasArmoryAssetDataset(baked))
        {
            return (_armoryOptions.AssetProxyUrl, true);
        }

        // No manager mirror — still wire the sidecar; uploads populate the volume directly.
        return (_armoryOptions.AssetProxyUrl, true);
    }

    /// <summary>
    /// The manager-local directory holding the stack's armory asset dataset (seeded into the stack's
    /// armory assets volume), or null when it is not present. A stack's uploaded dataset lives at
    /// <c>{ArmoryAssets:RootPath}/stacks/{stackId}/static/data</c>; when absent the manager falls back to the
    /// dataset baked into the manager image (<c>ArmoryOptions.SourcePath</c>'s <c>static/data/</c>).
    /// The sidecar is enabled when either the model-viewer metadata (<c>meta/</c>) or progression artwork
    /// (<c>progression/</c>) is present, so progression-only uploads still work without the 3D viewer data.
    /// </summary>
    private string? ArmoryAssetsSourceDir(string stackId)
    {
        // The stack's uploaded dataset (persistent, on the data volume) wins over the assets baked into
        // the manager image, so admins can supply assets without rebuilding the manager.
        var uploaded = _armoryAssetsOptions.DataPathFor(stackId);
        if (HasArmoryAssetDataset(uploaded))
        {
            return uploaded;
        }

        var dataDir = Path.Combine(_armoryOptions.SourcePath, "static", "data");
        return HasArmoryAssetDataset(dataDir) ? dataDir : null;
    }

    private static bool HasArmoryAssetDataset(string dataDir)
        => Directory.Exists(Path.Combine(dataDir, "meta"))
           || Directory.Exists(Path.Combine(dataDir, "progression"));

    /// <summary>
    /// Generates a fresh, cryptographically-random secret for signing the armory's player session
    /// cookies. Persisted per stack (see <see cref="ManagedStackEntity.ArmorySessionSecret"/>) so it is
    /// stable across restarts yet unrelated to any other credential.
    /// </summary>
    private static string GenerateArmorySessionSecret()
    {
        return Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }

    private async Task WaitForRunningServicesAsync(string stackId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LifecycleVerificationTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var containers = await GetContainersAsync(stackId, cancellationToken);
            if (HasRequiredRunningServices(containers))
            {
                return;
            }

            await Task.Delay(LifecyclePollInterval, cancellationToken);
        }

        throw new InvalidOperationException("Stack containers did not reach a running state before the startup timeout elapsed.");
    }

    private async Task WaitForDatabaseServiceAsync(string stackId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LifecycleVerificationTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var containers = await GetContainersAsync(stackId, cancellationToken);
            if (containers.Any(container =>
                    container.Name.Contains("database", StringComparison.OrdinalIgnoreCase) && IsRunning(container)))
            {
                return;
            }

            await Task.Delay(LifecyclePollInterval, cancellationToken);
        }

        throw new InvalidOperationException("Database container did not reach a running state before the startup timeout elapsed.");
    }

    /// <summary>
    /// Waits until MySQL inside the stack's database container accepts connections (not just that the
    /// container process is up). Used after db-import before starting auth/world.
    /// </summary>
    private async Task WaitForDatabaseReadyAsync(
        ManagedStackEntity stack,
        string stackId,
        CancellationToken cancellationToken)
    {
        var containerPrefix = DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName);
        var containerName = $"{containerPrefix}-database";
        var contextArg = await GetDockerContextArgAsync(stackId, cancellationToken);
        var deadline = DateTime.UtcNow + LifecycleVerificationTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var arguments =
                $"{contextArg}exec {containerName} mysqladmin ping -h127.0.0.1 -uroot " +
                $"-p{stack.DatabaseRootPassword} --silent";
            var (exitCode, _, _) = await RunDockerCliAsync(arguments, cancellationToken);
            if (exitCode == 0)
            {
                return;
            }

            await Task.Delay(LifecyclePollInterval, cancellationToken);
        }

        throw new InvalidOperationException("MySQL did not accept connections before the startup timeout elapsed.");
    }

    private async Task WaitForStackToStopAsync(string stackId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LifecycleVerificationTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var containers = await GetContainersAsync(stackId, cancellationToken);
            if (containers.Count == 0 || containers.All(container => !IsRunning(container)))
            {
                return;
            }

            await Task.Delay(LifecyclePollInterval, cancellationToken);
        }

        throw new InvalidOperationException("Stack containers did not stop before the shutdown timeout elapsed.");
    }

    private static bool HasRequiredRunningServices(IEnumerable<ContainerStatusDto> containers)
    {
        var runningContainers = containers
            .Where(IsRunning)
            .Select(container => container.Name)
            .ToList();

        return RequiredRunningServiceNames.All(serviceName =>
            runningContainers.Any(containerName =>
                containerName.Contains(serviceName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsRunning(ContainerStatusDto container)
    {
        return container.Status.Contains("running", StringComparison.OrdinalIgnoreCase)
            || container.Status.Contains("up", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureStackLifecycleAllowed(ManagedStackEntity stack, string operation)
    {
        if (stack.Status == StackStatus.Building)
        {
            throw new InvalidOperationException($"Cannot {operation} stack '{stack.StackName}' while it is building.");
        }
    }

    private static T? Deserialize<T>(string json)
    {
        // Tolerate blank columns (older rows / new nullable-less defaults) instead of throwing.
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static string NormalizeStackName(string stackName)
    {
        return stackName.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Removes all Docker resources owned by a stack on its engine: containers, named volumes, and images.
    /// Best-effort so delete never blocks on remote hiccups. Runs for both local and external stacks.
    /// </summary>
    private async Task CleanupStackDockerFootprintAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        var contextArg = string.Empty;
        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            try
            {
                contextArg = $"--context {_remoteEngine.GetContextName(stack.Id)} ";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve docker context for stack {StackId} cleanup", stack.Id);
                return;
            }
        }

        var project = DockerComposeOverrideGenerator.GetComposeProjectName(stack.Id);

        // Stop and remove any remaining containers for this compose project (covers cases where the
        // checkout was already deleted or compose down failed).
        await RunDockerBestEffortAsync(
            $"{contextArg}ps -aq --filter label=com.docker.compose.project={project}",
            cancellationToken,
            captureOutput: true,
            onStdout: async ids =>
            {
                var containerIds = ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var containerId in containerIds)
                {
                    await RunDockerBestEffortAsync($"{contextArg}rm -f {containerId}", cancellationToken);
                }
            });

        foreach (var volume in DockerComposeOverrideGenerator.GetAllStackVolumeNames(stack.Id))
        {
            await RunDockerBestEffortAsync($"{contextArg}volume rm -f {volume}", cancellationToken);
            _logger.LogInformation("Removed stack volume {Volume} during delete of stack {StackId}.", volume, stack.Id);
        }

        var removeSharedClientImage = !await _dbContext.ManagedStacks
            .AsNoTracking()
            .AnyAsync(s => s.Id != stack.Id && s.ClientEnabled, cancellationToken);

        foreach (var image in GetStackImageNames(stack, removeSharedClientImage))
        {
            await RunDockerBestEffortAsync($"{contextArg}rmi -f {image}", cancellationToken);
        }

        // Dangling build layers from this stack's compiles.
        await RunDockerBestEffortAsync($"{contextArg}image prune -f", cancellationToken);
    }

    private IEnumerable<string> GetStackImageNames(ManagedStackEntity stack, bool removeSharedClientImage)
    {
        var stackId = stack.Id;
        foreach (var repository in new[]
                 {
                     "acore/ac-wotlk-worldserver",
                     "acore/ac-wotlk-authserver",
                     "acore/ac-wotlk-db-import",
                     "acore/ac-wotlk-client-data",
                 })
        {
            yield return $"{repository}:{stackId}";
            yield return $"localhost/{repository}:{stackId}";
        }

        yield return _armoryImageService.ImageNameFor(stackId);

        if (removeSharedClientImage && !string.IsNullOrWhiteSpace(_clientServerOptions.ImageName))
        {
            yield return _clientServerOptions.ImageName;
        }
    }

    private void CleanupManagerPersistentData(string stackId)
    {
        foreach (var path in new[]
                 {
                     _clientOptions.StackGameDir(stackId),
                     _armoryAssetsOptions.StackRootPath(stackId),
                 })
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                _logger.LogInformation("Removed manager persistent data at {Path} during stack {StackId} delete.", path, stackId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove manager persistent data at {Path} during stack {StackId} delete.", path, stackId);
            }
        }
    }

    /// <summary>Runs a docker CLI command, swallowing all failures (used for best-effort cleanup).</summary>
    private async Task RunDockerBestEffortAsync(
        string arguments,
        CancellationToken cancellationToken,
        bool captureOutput = false,
        Func<string, Task>? onStdout = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return;
            }

            if (captureOutput && onStdout is not null)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    await onStdout(stdout);
                }

                return;
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort docker command failed: docker {Args}", arguments);
        }
    }

    // ===== Stack Import Methods =====
    
    /// <summary>
    /// Import a discovered stack into the manager database
    /// </summary>
    public async Task<StackDetailsDto> ImportDiscoveredStackAsync(
        string stackId, 
        ImportStackRequestDto request, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting import of stack {StackId} with name {StackName}", stackId, request.StackName);

        // 1. Discover the stack
        var discovered = await _stackDiscoveryService.DiscoverStackByIdAsync(stackId, cancellationToken);
        if (discovered == null)
        {
            throw new StackNotFoundException(stackId);
        }
        
        // Allow orphaned stacks - they will be imported with "Stopped" status
        // User can rebuild them later from the UI

        // 2. Validate no conflicts
        await ValidateImportAsync(stackId, discovered, cancellationToken);

        // 3. Create entity
        var entity = CreateEntityFromDiscovery(stackId, discovered, request);

        // 4. Save to database
        _dbContext.ManagedStacks.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Successfully imported stack {StackId} as {StackName}", 
            stackId, request.StackName);

        // 5. Return DTO
        return await MapAsync(entity, cancellationToken);
    }

    private async Task ValidateImportAsync(
        string stackId, 
        DiscoveredStackDto discovered, 
        CancellationToken cancellationToken)
    {
        // Check if stack ID already exists
        var existingStack = await _dbContext.ManagedStacks
            .FirstOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        
        if (existingStack != null)
        {
            throw new StackConflictException($"Stack with ID '{stackId}' already exists in the database");
        }

        // Validate git information is present (critical for updates)
        if (string.IsNullOrEmpty(discovered.CoreRepositoryUrl))
        {
            _logger.LogWarning(
                "Stack {StackId} has no git repository URL. Updates will not work correctly.", 
                stackId);
        }
        
        if (string.IsNullOrEmpty(discovered.CoreCommitSha))
        {
            _logger.LogWarning(
                "Stack {StackId} has no git commit SHA. Unable to determine current version.", 
                stackId);
        }

        // Validate ServerType matches discovered git info
        ValidateServerTypeConsistency(discovered);

        // Check for port conflicts
        var allStacks = await _dbContext.ManagedStacks.ToListAsync(cancellationToken);
        
        foreach (var stack in allStacks)
        {
            if (stack.DatabasePort == discovered.DatabasePort)
            {
                throw new StackConflictException(
                    $"Database port {discovered.DatabasePort} is already in use by stack '{stack.StackName}'");
            }
            if (stack.AuthServerPort == discovered.AuthServerPort)
            {
                throw new StackConflictException(
                    $"Auth server port {discovered.AuthServerPort} is already in use by stack '{stack.StackName}'");
            }
            if (stack.WorldServerPort == discovered.WorldServerPort)
            {
                throw new StackConflictException(
                    $"World server port {discovered.WorldServerPort} is already in use by stack '{stack.StackName}'");
            }
            if (stack.SoapPort == discovered.SoapPort)
            {
                throw new StackConflictException(
                    $"SOAP port {discovered.SoapPort} is already in use by stack '{stack.StackName}'");
            }
        }
    }

    private void ValidateServerTypeConsistency(DiscoveredStackDto discovered)
    {
        if (string.IsNullOrEmpty(discovered.CoreRepositoryUrl))
        {
            return; // Already logged warning above
        }

        var normalizedUrl = discovered.CoreRepositoryUrl.TrimEnd('/').ToLowerInvariant();
        if (normalizedUrl.EndsWith(".git"))
        {
            normalizedUrl = normalizedUrl[..^4];
        }

        // Validate Playerbots
        if (discovered.InferredServerType == ServerType.Playerbots)
        {
            if (!normalizedUrl.Contains("mod-playerbots/azerothcore-wotlk"))
            {
                _logger.LogError(
                    "Stack {StackId} detected as Playerbots but repository URL doesn't match: {Url}. " +
                    "Updates may fail.", 
                    discovered.StackId, discovered.CoreRepositoryUrl);
            }
            
            if (!string.IsNullOrEmpty(discovered.CoreBranch) && 
                !discovered.CoreBranch.Equals("Playerbot", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Stack {StackId} detected as Playerbots but branch is '{Branch}' (expected 'Playerbot'). " +
                    "Updates may use incorrect branch.", 
                    discovered.StackId, discovered.CoreBranch);
            }
        }
        // Validate Standard
        else if (discovered.InferredServerType == ServerType.Standard)
        {
            if (normalizedUrl.Contains("mod-playerbots"))
            {
                _logger.LogWarning(
                    "Stack {StackId} has mod-playerbots in URL but was detected as Standard type. " +
                    "This may be due to branch mismatch. URL: {Url}, Branch: {Branch}", 
                    discovered.StackId, discovered.CoreRepositoryUrl, discovered.CoreBranch);
            }
        }
    }

    private string GetDefaultBranchForServerType(ServerType serverType)
        => _serverTypeCatalog.GetCoreBranch(serverType);

    private ManagedStackEntity CreateEntityFromDiscovery(
        string stackId,
        DiscoveredStackDto discovered,
        ImportStackRequestDto request)
    {
        var now = DateTime.UtcNow;

        return new ManagedStackEntity
        {
            Id = stackId,
            StackName = request.StackName,
            NormalizedStackName = request.StackName.ToUpperInvariant(),
            ServerType = discovered.InferredServerType,
            Status = discovered.IsOrphaned ? StackStatus.Stopped : discovered.CurrentStatus,
            
            // Ports from discovery
            DatabasePort = discovered.DatabasePort,
            AuthServerPort = discovered.AuthServerPort,
            WorldServerPort = discovered.WorldServerPort,
            SoapPort = discovered.SoapPort,
            
            // Passwords - use provided, discovered, or generate secure ones
            DatabaseRootPassword = request.DatabaseRootPassword 
                ?? discovered.DiscoveredDatabasePassword 
                ?? GenerateSecurePassword(),
            SoapUsername = discovered.DiscoveredSoapUsername ?? GenerateSoapUsername(stackId),
            SoapPassword = discovered.DiscoveredSoapPassword ?? GenerateSecureSoapPassword(),
            
            // Defaults
            MaxPlayers = 100,
            RealmName = "AzerothCore",
            ModuleIdsJson = JsonSerializer.Serialize(discovered.DiscoveredModules ?? new List<string>()),
            CustomEnvVarsJson = JsonSerializer.Serialize(discovered.DiscoveredEnvVars ?? new Dictionary<string, string>()),
            
            // Version info from git
            // IMPORTANT: Use discovered branch if available, otherwise infer from ServerType
            CoreRepositoryUrl = discovered.CoreRepositoryUrl ?? string.Empty,
            CoreBranch = !string.IsNullOrEmpty(discovered.CoreBranch) 
                ? discovered.CoreBranch 
                : GetDefaultBranchForServerType(discovered.InferredServerType),
            CoreCommitSha = discovered.CoreCommitSha ?? string.Empty,
            
            // Timestamps
            CreatedAt = now,
            LastBuiltAt = null, // Unknown when it was actually built
            
            // Will be populated on next update check
            IsOutdated = false,
            IsCoreOutdated = false,
            OutdatedModuleCount = 0,
            ModuleVersionsJson = "[]",
            OutdatedModulesJson = "[]"
        };
    }

    public async Task<SoapCredentialsDto?> InitializeAdminAccountAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (stack is null)
        {
            throw new StackNotFoundException($"Stack with ID '{stackId}' not found.");
        }

        // Check if already initialized
        if (stack.IsAdminAccountInitialized)
        {
            _logger.LogInformation("Admin account for stack {StackId} already initialized", stackId);
            return null;
        }

        // Verify stack is running
        var composeProjectName = DockerComposeOverrideGenerator.GetComposeProjectName(stackId);
        var dockerContext = await ResolveDockerContextAsync(stack, cancellationToken);
        var stackContainers = await _dockerService.ListContainersAsync(composeProjectName, dockerContext, cancellationToken);

        if (!stackContainers.Any())
        {
            throw new InvalidOperationException($"Stack {stackId} has no running containers");
        }

        var databaseContainer = stackContainers
            .FirstOrDefault(c => c.Name.Contains("database", StringComparison.OrdinalIgnoreCase));

        if (databaseContainer is null)
        {
            throw new InvalidOperationException($"Database container not found for stack {stackId}");
        }

        if (!databaseContainer.Status.Contains("running", StringComparison.OrdinalIgnoreCase) &&
            !databaseContainer.Status.Contains("up", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Database container is not running (status: {databaseContainer.Status})");
        }

        var username = stack.SoapUsername;
        var password = stack.SoapPassword;
        
        _logger.LogInformation("Creating admin account for stack {StackId} with SRP6 credentials", stackId);
        
        try
        {
            // Calculate SRP6 salt and verifier
            var (salt, verifier) = CalculateSrp6Credentials(username, password);
            
            // Convert to hex strings for SQL
            var saltHex = BitConverter.ToString(salt).Replace("-", "");
            var verifierHex = BitConverter.ToString(verifier).Replace("-", "");
            
            // SQL to create admin account with SRP6 credentials
            var sql = $@"
                INSERT INTO acore_auth.account (username, salt, verifier, email, reg_mail, joindate, expansion)
                VALUES ('{username}', UNHEX('{saltHex}'), UNHEX('{verifierHex}'), '', '', NOW(), 2)
                ON DUPLICATE KEY UPDATE 
                    salt = UNHEX('{saltHex}'), 
                    verifier = UNHEX('{verifierHex}');
                
                INSERT INTO acore_auth.account_access (id, gmlevel, RealmID)
                SELECT id, 3, -1 FROM acore_auth.account WHERE username = '{username}'
                ON DUPLICATE KEY UPDATE gmlevel = 3, RealmID = -1;
            ";
            
            // Execute SQL via docker exec, targeting the stack's engine (external stacks run on the
            // remote engine over the SSH docker context; local stacks use the default engine).
            var contextArg = dockerContext is null ? string.Empty : $"--context {dockerContext} ";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"{contextArg}exec -i {databaseContainer.Name} mysql -uroot -p{stack.DatabaseRootPassword} -e \"{sql.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                // Filter out password warning
                var actualError = string.Join("\n", error.Split('\n')
                    .Where(line => !line.Contains("Using a password on the command line")));
                
                if (!string.IsNullOrWhiteSpace(actualError))
                {
                    _logger.LogError("Failed to create admin account in database: {Error}", actualError);
                    throw new InvalidOperationException($"Failed to create admin account: {actualError}");
                }
            }

            _logger.LogInformation("Admin account created successfully for stack {StackId}", stackId);

            // Mark as initialized
            stack.IsAdminAccountInitialized = true;
            stack.AdminAccountInitializedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Write credentials file to stack directory as secondary backup
            WriteCredentialsFile(stackId, stack.StackName, username, password);

            return new SoapCredentialsDto { Username = username, Password = password };
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Error creating admin account for stack {StackId}", stackId);
            throw new InvalidOperationException($"Failed to create admin account: {ex.Message}", ex);
        }
    }

    public async Task<SoapCredentialsDto?> GetSoapCredentialsAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (stack is null) return null;

        // Audit: revealing a stored secret is a sensitive operation.
        _logger.LogWarning("SOAP credentials revealed for stack {StackId} ({StackName})", stack.Id, stack.StackName);
        return new SoapCredentialsDto { Username = stack.SoapUsername, Password = stack.SoapPassword };
    }

    public async Task<DatabaseCredentialsDto?> GetDatabaseCredentialsAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (stack is null) return null;

        // Audit: revealing a stored secret is a sensitive operation.
        _logger.LogWarning("Database root credentials revealed for stack {StackId} ({StackName})", stack.Id, stack.StackName);
        return new DatabaseCredentialsDto
        {
            Username = "root",
            Password = stack.DatabaseRootPassword,
            Port = stack.DatabasePort,
        };
    }

    private void WriteCredentialsFile(string stackId, string stackName, string username, string password)
    {
        try
        {
            var stackPath = GetStackPath(stackId);
            Directory.CreateDirectory(stackPath);
            var filePath = Path.Combine(stackPath, "soap-credentials.txt");
            var content = $"""
                # Azeroth Platform — SOAP Admin Credentials
                # Stack: {stackName} ({stackId})
                # Created: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
                #
                # WARNING: Keep this file secure. Anyone with these credentials can
                # run admin commands on your AzerothCore server via the SOAP interface.

                Username: {username}
                Password: {password}
                """;
            File.WriteAllText(filePath, content);
            _logger.LogInformation("Wrote SOAP credentials backup to {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            // Non-fatal: credentials are already persisted in the database
            _logger.LogWarning(ex, "Failed to write SOAP credentials file for stack {StackId}", stackId);
        }
    }
    
    private static (byte[] salt, byte[] verifier) CalculateSrp6Credentials(string username, string password)
    {
        // WoW uses username:password in UPPERCASE for SRP6
        var identity = $"{username.ToUpperInvariant()}:{password.ToUpperInvariant()}";
        
        // Generate random 32-byte salt
        var salt = new byte[32];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }
        
        // Calculate x = H(salt, H(identity))
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var identityHash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity));
        
        var saltAndHash = new byte[salt.Length + identityHash.Length];
        Array.Copy(salt, 0, saltAndHash, 0, salt.Length);
        Array.Copy(identityHash, 0, saltAndHash, salt.Length, identityHash.Length);
        
        var xHash = sha1.ComputeHash(saltAndHash);
        var x = new System.Numerics.BigInteger(xHash, isUnsigned: true, isBigEndian: false);
        
        // SRP6 constants for WoW
        var N = System.Numerics.BigInteger.Parse("0894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7", System.Globalization.NumberStyles.HexNumber);
        var g = new System.Numerics.BigInteger(7);
        
        // Calculate verifier = g^x mod N
        var verifier = System.Numerics.BigInteger.ModPow(g, x, N);
        
        // Convert to 32-byte little-endian format
        var verifierBytes = verifier.ToByteArray(isUnsigned: true, isBigEndian: false);
        
        // Ensure exactly 32 bytes (pad with zeros if needed, truncate if too long)
        var verifierResult = new byte[32];
        Array.Copy(verifierBytes, 0, verifierResult, 0, Math.Min(verifierBytes.Length, 32));
        
        return (salt, verifierResult);
    }

    private static string GenerateSecurePassword(int length = 32)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
        var password = new char[length];
        
        for (int i = 0; i < length; i++)
        {
            password[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }
        
        return new string(password);
    }

    private static string GenerateSecureSoapPassword(int length = 32)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var password = new char[length];
        for (int i = 0; i < length; i++)
            password[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        return new string(password);
    }

    /// <summary>Generates a unique SOAP username derived from the stack ID.</summary>
    private static string GenerateSoapUsername(string stackId)
        => $"acmgr_{stackId[..Math.Min(8, stackId.Length)]}";

    private void ApplyArmoryEmailSettings(ManagedStackEntity stack, ArmoryAccountsConfigDto? accounts)
    {
        accounts = ArmoryEmailConfigDefaults.NormalizeAccounts(accounts);
        stack.ArmoryUseEmailConfirmation = accounts.UseEmailConfirmation;
        if (!accounts.UseEmailConfirmation)
        {
            stack.ArmoryEmailConfigured = false;
            stack.ArmoryEmailConfigJson = string.Empty;
            stack.ArmoryEmailSmtpPasswordProtected = string.Empty;
            return;
        }

        stack.ArmoryEmailConfigured = accounts.EmailConfigured;
        if (accounts.Email is null)
        {
            stack.ArmoryEmailConfigJson = string.Empty;
            return;
        }

        var publicEmail = ArmoryEmailConfigDefaults.ToPublicDto(accounts.Email);
        stack.ArmoryEmailConfigJson = ArmoryEmailConfigDefaults.SerializeEmailConfig(publicEmail);
        if (!string.IsNullOrWhiteSpace(accounts.Email.SmtpPassword))
        {
            stack.ArmoryEmailSmtpPasswordProtected = _secretProtector.Protect(accounts.Email.SmtpPassword);
        }
    }

    private static ArmoryAccountsConfigDto MapArmoryAccountsConfig(ManagedStackEntity stack)
    {
        var email = ArmoryEmailConfigDefaults.DeserializeEmailConfig(stack.ArmoryEmailConfigJson);
        return new ArmoryAccountsConfigDto
        {
            UseEmailConfirmation = stack.ArmoryUseEmailConfirmation,
            EmailConfigured = stack.ArmoryEmailConfigured,
            Email = email is null ? null : ArmoryEmailConfigDefaults.ToPublicDto(email),
        };
    }

    public async Task<bool> ApplyModuleConfigAsync(string stackId, Dictionary<string, string> envVars, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (stack is null)
        {
            throw new StackNotFoundException($"Stack with ID '{stackId}' not found.");
        }

        var existing = Deserialize<Dictionary<string, string>>(stack.CustomEnvVarsJson)
            ?? new Dictionary<string, string>();

        foreach (var (key, value) in envVars)
        {
            existing[key] = value;
        }

        stack.CustomEnvVarsJson = JsonSerializer.Serialize(existing, JsonOptions);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Applied {Count} module env var(s) to stack {StackId}", envVars.Count, stackId);

        // Regenerate runtime config files if stack has been built
        var stackPath = GetStackPath(stackId);
        var repoPath = Path.Combine(stackPath, "azerothcore-wotlk");
        if (Directory.Exists(repoPath))
        {
            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken);
        }

        return true;
    }

    public async Task<RemoteSetupResultDto?> SyncVpcFirewallAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null || stack.DeploymentTarget != DeploymentTarget.External)
        {
            return null;
        }

        return await SyncExternalWebFirewallAsync(stack, cancellationToken);
    }

    /// <summary>
    /// Best-effort ufw sync when external web ports change or the armory comes online. Failures are logged
    /// only — starting the armory must not fail because SSH/ufw hiccuped.
    /// </summary>
    private async Task TrySyncExternalWebFirewallAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        try
        {
            await SyncExternalWebFirewallAsync(stack, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync VPC host firewall for external stack {StackId}.", stack.Id);
        }
    }

    private async Task<RemoteSetupResultDto?> SyncExternalWebFirewallAsync(
        ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(stack.ExternalSshPrivateKey))
        {
            throw new InvalidOperationException("External stack is missing SSH credentials.");
        }

        var privateKey = _secretProtector.Unprotect(stack.ExternalSshPrivateKey);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
        return await _remoteEngine.SyncRemoteHostFirewallAsync(
            stack.ExternalHost,
            stack.ExternalSshPort,
            stack.ExternalSshUser,
            privateKey,
            new RemoteSetupOptionsDto
            {
                RemoteOs = RemoteHostOs.Linux,
                AuthServerPort = stack.AuthServerPort,
                WorldServerPort = stack.WorldServerPort,
                ArmoryPort = stack.ArmoryPort,
                ClientPort = stack.ClientPort,
                SshPort = stack.ExternalSshPort
            },
            timeoutCts.Token);
    }

    public async Task<VpcSecurityProfileDto?> GetVpcSecurityProfileAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null || stack.DeploymentTarget != DeploymentTarget.External)
        {
            return null;
        }

        return VpcSecurityCatalog.BuildProfile(
            stack.ExternalHost,
            stack.AuthServerPort,
            stack.WorldServerPort,
            stack.ArmoryPort,
            stack.ClientPort,
            stack.DatabasePort,
            stack.SoapPort,
            stack.ExternalSshPort);
    }

    private string ResolveExternalDataPlaneBind(ManagedStackEntity stack)
    {
        // Docker port publishing requires a numeric bind IP (or no prefix for all interfaces). ExternalHost
        // is often a DNS name (e.g. ec2-…compute.amazonaws.com) used for SSH/realmlist — never pass that
        // through to compose.
        var configured = TryParseBindAddress(_dockerOptions.ExternalDataPlaneBindAddress);
        if (configured is not null)
        {
            return configured;
        }

        if (!string.IsNullOrWhiteSpace(_dockerOptions.ExternalDataPlaneBindAddress))
        {
            _logger.LogWarning(
                "Docker:ExternalDataPlaneBindAddress '{Bind}' is not a valid IP; ignoring.",
                _dockerOptions.ExternalDataPlaneBindAddress.Trim());
        }

        var externalHostIp = TryParseBindAddress(stack.ExternalHost);
        if (externalHostIp is not null)
        {
            return externalHostIp;
        }

        // Unset or hostname: publish MySQL/SOAP on all remote interfaces (see DockerOptions).
        return string.Empty;
    }

    /// <summary>Returns a bind IP when <paramref name="value"/> is blank or a valid IP; null for hostnames.</summary>
    private static string? TryParseBindAddress(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return System.Net.IPAddress.TryParse(trimmed, out _) ? trimmed : null;
    }
}
