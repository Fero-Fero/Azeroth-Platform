using System.Collections.Concurrent;
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
    private const int DatabaseReadyConsecutivePings = 3;
    /// <summary>Max time to wait for an external stack's Docker engine probe during a detail refresh.</summary>
    private static readonly TimeSpan ExternalRuntimeProbeTimeout = TimeSpan.FromSeconds(45);
    /// <summary>How often to re-verify the SOAP admin row during stack detail refreshes (MySQL over SSH is slow).</summary>
    private static readonly TimeSpan SoapAdminReconcileMinInterval = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, DateTime> LastSoapAdminReconcileAt = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ExternalRuntimeProbeCache> ExternalRuntimeProbeCaches = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, StackDetailsDto> StackListStatusCaches = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ExternalProbeLocks = new(StringComparer.Ordinal);
    private static readonly TimeSpan ExternalRuntimeProbeCacheTtl = TimeSpan.FromMinutes(3);

    private sealed record ExternalRuntimeProbeCache(
        List<ContainerStatusDto> Containers,
        List<StackServiceDto> Services,
        StackStatus RuntimeStatus,
        bool? EngineReachable,
        string? EngineError,
        DateTime CachedAt);

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
    private readonly IArmoryDatabaseProvisioningService _armoryDatabase;
    private readonly IMySqlConnectionFactory _connectionFactory;
    private readonly ICloudSshKeyService _cloudSshKeyService;
    private readonly ICloudInstanceLifecycleService _cloudInstanceLifecycle;

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
        IStackImageShippingService stackImageShipping,
        IArmoryDatabaseProvisioningService armoryDatabase,
        IMySqlConnectionFactory connectionFactory,
        ICloudSshKeyService cloudSshKeyService,
        ICloudInstanceLifecycleService cloudInstanceLifecycle)
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
        _armoryDatabase = armoryDatabase;
        _connectionFactory = connectionFactory;
        _cloudSshKeyService = cloudSshKeyService;
        _cloudInstanceLifecycle = cloudInstanceLifecycle;
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
            .AsNoTracking()
            .OrderByDescending(stack => stack.CreatedAt)
            .ToListAsync(cancellationToken);

        // The stacks overview only needs DB-backed status and config. Probing Docker on every stack
        // (especially external VPC engines over SSH) makes the list unbearably slow.
        var stackDtos = new StackDetailsDto[stacks.Count];
        await Task.WhenAll(stacks.Select(async (stack, index) =>
        {
            if (StackListStatusCaches.TryGetValue(stack.Id, out var cached))
            {
                stackDtos[index] = cached;
                return;
            }

            stackDtos[index] = await MapAsync(stack, probeRuntime: false, cancellationToken);
        }));

        return stackDtos;
    }

    public async Task<IReadOnlyList<StackDetailsDto>> ProbeAllStacksForListAsync(
        CancellationToken cancellationToken = default)
    {
        var stacks = await _dbContext.ManagedStacks
            .AsNoTracking()
            .OrderByDescending(stack => stack.CreatedAt)
            .ToListAsync(cancellationToken);

        if (stacks.Count == 0)
        {
            return Array.Empty<StackDetailsDto>();
        }

        using var probeGate = new SemaphoreSlim(4);
        var stackDtos = new StackDetailsDto[stacks.Count];
        await Task.WhenAll(stacks.Select(async (stack, index) =>
        {
            await probeGate.WaitAsync(cancellationToken);
            try
            {
                var probeRuntime = stack.Status != StackStatus.SetupIncomplete;
                var dto = await MapAsync(stack, probeRuntime, cancellationToken);
                StackListStatusCaches[stack.Id] = dto;
                if (probeRuntime)
                {
                    await PersistProbedStackStatusAsync(stack.Id, dto.Status, cancellationToken);
                }
                stackDtos[index] = dto;
            }
            finally
            {
                probeGate.Release();
            }
        }));

        return stackDtos;
    }

    public async Task<StackDetailsDto?> GetAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return null;
        }

        var dto = await MapAsync(stack, probeRuntime: true, cancellationToken, preferCachedRuntimeProbe: true);
        StackListStatusCaches[stackId] = dto;
        await TryReconcileStaleFailedStatusAsync(stackId, dto.Status, cancellationToken);
        return dto;
    }

    private async Task PersistProbedStackStatusAsync(
        string stackId,
        StackStatus probedStatus,
        CancellationToken cancellationToken)
    {
        if (_stackJobService.GetStatus(stackId) is { IsRunning: true })
        {
            return;
        }

        if (probedStatus is StackStatus.Starting or StackStatus.Initializing or StackStatus.Building)
        {
            return;
        }

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null || stack.Status == probedStatus || stack.Status == StackStatus.SetupIncomplete)
        {
            return;
        }

        stack.Status = probedStatus;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Clears a stale <see cref="StackStatus.Failed"/> marker once the stack is actually stopped again
    /// (common after a VPC reboot or a failed start while SSH was down).
    /// </summary>
    private async Task TryReconcileStaleFailedStatusAsync(
        string stackId, StackStatus displayedStatus, CancellationToken cancellationToken)
    {
        if (displayedStatus != StackStatus.Stopped)
        {
            return;
        }

        if (_stackJobService.GetStatus(stackId) is { IsRunning: true })
        {
            return;
        }

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null || stack.Status != StackStatus.Failed)
        {
            return;
        }

        stack.Status = StackStatus.Stopped;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<StackDetailsDto> CreateAsync(StackConfigurationDto configuration, CancellationToken cancellationToken = default)
    {
        var existingDraft = await TryGetSetupDraftEntityAsync(configuration.DraftStackId, cancellationToken);
        var stackId = existingDraft?.Id ?? Guid.NewGuid().ToString("N");
        var deployment = configuration.Deployment ?? new DeploymentConfigDto();
        var externalHost = RealmlistHostResolver.NormalizeHost(deployment.ExternalHost ?? string.Empty);

        // Realmlist host resolution: explicit value wins; otherwise for External stacks default to the
        // remote host so clients are pointed at the droplet. Local stacks fall back to the global
        // default (Migrations:RealmlistHost) when left blank.
        var realmlistHost = RealmlistHostResolver.NormalizeHost(configuration.Advanced.RealmlistHost ?? string.Empty);
        if (string.IsNullOrWhiteSpace(realmlistHost) && deployment.Target == DeploymentTarget.External)
        {
            realmlistHost = externalHost;
        }

        if (!string.IsNullOrWhiteSpace(realmlistHost))
        {
            realmlistHost = RealmlistHostResolver.ResolveForRealmAddress(realmlistHost, cancellationToken);
        }

        var armoryPort = existingDraft is { ArmoryPort: > 0 }
            ? existingDraft.ArmoryPort
            : await AllocateStackPortAsync(cancellationToken, StackNetworkDefaults.DefaultArmoryPort);
        var clientPort = existingDraft is { ClientPort: > 0 }
            ? existingDraft.ClientPort
            : await AllocateStackPortAsync(cancellationToken, StackNetworkDefaults.DefaultClientPort, armoryPort);

        var serviceEnvJson = BuildEnvJson(configuration.Advanced);

        var protectedSshPrivateKey = string.Empty;
        if (deployment.Target == DeploymentTarget.External)
        {
            var resolvedPrivateKey = await ResolveAndMaybeVaultDeploymentKeyAsync(deployment, cancellationToken);
            protectedSshPrivateKey = _secretProtector.Protect(resolvedPrivateKey);
        }

        var stack = existingDraft ?? new ManagedStackEntity { Id = stackId, CreatedAt = DateTime.UtcNow };
        stack.Id = stackId;
        stack.StackName = configuration.StackName.Trim();
        stack.NormalizedStackName = NormalizeStackName(configuration.StackName);
        stack.ServerType = configuration.ServerType;
        stack.Status = StackStatus.Stopped;
        stack.ModuleIdsJson = JsonSerializer.Serialize(configuration.ModuleIds, JsonOptions);
        stack.DatabaseRootPassword = configuration.Database.RootPassword;
        stack.DatabasePort = configuration.Database.Port;
        stack.AuthServerPort = configuration.Ports.AuthServer;
        stack.WorldServerPort = configuration.Ports.WorldServer;
        stack.SoapPort = configuration.Ports.SoapPort;
        stack.ArmoryPort = armoryPort;
        stack.ClientPort = clientPort;
        stack.ClientEnabled = true;
        stack.MaxPlayers = configuration.Advanced.MaxPlayers;
        stack.RealmName = configuration.Advanced.RealmName.Trim();
        stack.ServiceEnvVarsJson = serviceEnvJson;
        stack.RealmlistHostOverride = realmlistHost;
        stack.DeploymentTarget = deployment.Target;
        stack.ExternalHost = externalHost;
        stack.ExternalSshPort = deployment.ExternalSshPort <= 0 ? 22 : deployment.ExternalSshPort;
        stack.ExternalSshUser = (deployment.ExternalSshUser ?? string.Empty).Trim();
        stack.WizardDraftJson = string.Empty;
        stack.WizardStepId = string.Empty;
        if (existingDraft is null)
        {
            stack.SoapUsername = GenerateSoapUsername(stackId);
            stack.SoapPassword = GenerateSecureSoapPassword();
            stack.ExternalSshPrivateKey = protectedSshPrivateKey;
            _dbContext.ManagedStacks.Add(stack);
        }
        else if (!string.IsNullOrWhiteSpace(protectedSshPrivateKey))
        {
            stack.ExternalSshPrivateKey = protectedSshPrivateKey;
        }
        ApplyCloudBinding(stack, deployment, replaceEmpty: true);

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
        return await MapAsync(stack, probeRuntime: true, cancellationToken);
    }

    public async Task<StackDetailsDto> SaveSetupDraftAsync(
        StackSetupDraftRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deployment = request.Deployment ?? new DeploymentConfigDto();
        if (deployment.Target != DeploymentTarget.External)
        {
            throw new InvalidOperationException("Unfinished VPC stacks are only created for external deployments.");
        }

        var host = RealmlistHostResolver.NormalizeHost(deployment.ExternalHost ?? string.Empty);
        var instanceId = (deployment.CloudInstanceId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(instanceId))
        {
            throw new InvalidOperationException("Launch or select a VPC instance before saving an unfinished stack.");
        }

        var requestedId = (request.StackId ?? string.Empty).Trim();
        ManagedStackEntity? stack = null;
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            stack = await _dbContext.ManagedStacks
                .SingleOrDefaultAsync(item => item.Id == requestedId, cancellationToken);
            if (stack is not null && stack.Status != StackStatus.SetupIncomplete)
            {
                throw new InvalidOperationException("That stack is already set up.");
            }
        }

        if (stack is null && !string.IsNullOrWhiteSpace(instanceId))
        {
            stack = await _dbContext.ManagedStacks
                .SingleOrDefaultAsync(
                    item => item.Status == StackStatus.SetupIncomplete && item.CloudInstanceId == instanceId,
                    cancellationToken);
        }

        var isNew = stack is null;
        stack ??= new ManagedStackEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            Status = StackStatus.SetupIncomplete,
            ServerType = ServerType.Standard,
            ModuleIdsJson = "[]",
            ServiceEnvVarsJson = "{}",
            AppliedPatchesJson = "[]",
            ClientEnabled = true,
            RealmName = "AzerothCore",
            MaxPlayers = 100,
            ExternalSshPort = 22,
        };

        if (string.IsNullOrWhiteSpace(deployment.CloudProvider)
            && !string.IsNullOrWhiteSpace(deployment.CloudConnectionId))
        {
            var connection = await _dbContext.CloudProviderConnections.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == deployment.CloudConnectionId, cancellationToken);
            if (connection is not null)
            {
                deployment.CloudProvider = connection.Provider;
            }
        }

        var stackName = await ResolveDraftStackNameAsync(stack, request.StackName, cancellationToken);

        stack.StackName = stackName;
        stack.NormalizedStackName = NormalizeStackName(stackName);
        stack.Status = StackStatus.SetupIncomplete;
        stack.DeploymentTarget = DeploymentTarget.External;
        stack.ExternalHost = host;
        stack.ExternalSshPort = deployment.ExternalSshPort <= 0 ? 22 : deployment.ExternalSshPort;
        stack.ExternalSshUser = (deployment.ExternalSshUser ?? string.Empty).Trim();
        stack.WizardStepId = NormalizeWizardStepId(request.WizardStepId);
        stack.WizardDraftJson = RedactWizardDraftPrivateKey(request.WizardDraftJson);
        ApplyCloudBinding(stack, deployment, replaceEmpty: true);

        if (!string.IsNullOrWhiteSpace(deployment.ExternalSshPrivateKey)
            || !string.IsNullOrWhiteSpace(deployment.SavedSshKeyId))
        {
            try
            {
                var resolved = await ResolveAndMaybeVaultDeploymentKeyAsync(deployment, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    stack.ExternalSshPrivateKey = _secretProtector.Protect(resolved);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve SSH key while saving setup draft {StackId}.", stack.Id);
            }
        }

        if (isNew)
        {
            stack.SoapUsername = GenerateSoapUsername(stack.Id);
            stack.SoapPassword = GenerateSecureSoapPassword();
            _dbContext.ManagedStacks.Add(stack);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(stack, probeRuntime: false, cancellationToken);
    }

    public async Task<StackSetupDraftDto?> GetSetupDraftAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null || stack.Status != StackStatus.SetupIncomplete)
        {
            return null;
        }

        var privateKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(stack.ExternalSshPrivateKey))
        {
            try
            {
                privateKey = _secretProtector.Unprotect(stack.ExternalSshPrivateKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not decrypt SSH key for setup draft {StackId}.", stack.Id);
            }
        }

        return new StackSetupDraftDto
        {
            StackId = stack.Id,
            StackName = stack.StackName,
            WizardStepId = string.IsNullOrWhiteSpace(stack.WizardStepId) ? "deployment" : stack.WizardStepId,
            WizardDraftJson = string.IsNullOrWhiteSpace(stack.WizardDraftJson) ? "{}" : stack.WizardDraftJson,
            ExternalSshPrivateKey = privateKey,
            Deployment = new DeploymentConfigDto
            {
                Target = DeploymentTarget.External,
                ExternalHost = stack.ExternalHost,
                ExternalSshPort = stack.ExternalSshPort == 0 ? 22 : stack.ExternalSshPort,
                ExternalSshUser = stack.ExternalSshUser,
                CloudConnectionId = stack.CloudConnectionId,
                CloudInstanceId = stack.CloudInstanceId,
                CloudRegion = stack.CloudRegion,
                CloudProvider = stack.CloudProvider,
                CloudInstanceType = stack.CloudInstanceType,
            },
        };
    }

    private async Task<ManagedStackEntity?> TryGetSetupDraftEntityAsync(
        string? draftStackId,
        CancellationToken cancellationToken)
    {
        var id = (draftStackId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (stack is null || stack.Status != StackStatus.SetupIncomplete)
        {
            throw new InvalidOperationException("The unfinished VPC stack was not found or is already set up.");
        }

        return stack;
    }

    private static string NormalizeWizardStepId(string? stepId)
    {
        var id = (stepId ?? string.Empty).Trim().ToLowerInvariant();
        return id is "deployment" or "server-config" or "modules" or "database" or "ports"
            or "advanced" or "email" or "review"
            ? id
            : "deployment";
    }

    private async Task<string> ResolveDraftStackNameAsync(
        ManagedStackEntity stack,
        string? requestedName,
        CancellationToken cancellationToken)
    {
        var userName = TrySanitizeDraftStackName(requestedName);
        if (userName is not null && IsPlaceholderDraftStackName(userName))
        {
            userName = null;
        }

        string stackName;
        if (!string.IsNullOrWhiteSpace(userName))
        {
            stackName = userName;
        }
        else if (!string.IsNullOrWhiteSpace(stack.StackName) && !IsPlaceholderDraftStackName(stack.StackName))
        {
            stackName = stack.StackName;
        }
        else
        {
            stackName = $"unnamed-instance-{stack.Id[..8]}";
        }

        var nameTaken = await _dbContext.ManagedStacks.AnyAsync(
            item => item.Id != stack.Id && item.NormalizedStackName == NormalizeStackName(stackName),
            cancellationToken);
        if (nameTaken)
        {
            stackName = $"unnamed-instance-{stack.Id[..8]}";
        }

        return stackName;
    }

    private static bool IsPlaceholderDraftStackName(string? value)
    {
        var name = (value ?? string.Empty).Trim().ToLowerInvariant();
        return name.StartsWith("unnamed-instance", StringComparison.Ordinal)
               || (name.StartsWith("vpc-", StringComparison.Ordinal) && name.Length <= 12);
    }

    private static string DisplayNameFor(ManagedStackEntity stack)
        => stack.Status == StackStatus.SetupIncomplete && IsPlaceholderDraftStackName(stack.StackName)
            ? "Unnamed instance"
            : stack.StackName;

    private static string? TrySanitizeDraftStackName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length > 50)
        {
            slug = slug[..50].TrimEnd('-');
        }

        return slug.Length >= 3 ? slug : null;
    }

    private static string RedactWizardDraftPrivateKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteJsonRedactingSshKey(doc.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static void WriteJsonRedactingSshKey(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.NameEquals("externalSshPrivateKey"))
                    {
                        writer.WriteStringValue(string.Empty);
                    }
                    else
                    {
                        WriteJsonRedactingSshKey(property.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteJsonRedactingSshKey(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
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
        stack.ServiceEnvVarsJson = BuildEnvJson(configuration.Advanced);
        stack.RealmlistHostOverride = RealmlistHostResolver.NormalizeHost(configuration.Advanced.RealmlistHost ?? string.Empty);
        ApplyArmoryEmailSettings(stack, configuration.ArmoryAccounts);

        // Post-create deployment editing: the target itself is fixed (flipping local<->external is a
        // migration, not an edit), but an external stack's connection details can be updated. A blank
        // private key means "keep the existing one" so the UI never has to round-trip the secret.
        if (configuration.Deployment is not null && stack.DeploymentTarget == DeploymentTarget.External)
        {
            var d = configuration.Deployment;
            if (!string.IsNullOrWhiteSpace(d.ExternalHost))
            {
                stack.ExternalHost = RealmlistHostResolver.NormalizeHost(d.ExternalHost);
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

            ApplyCloudBinding(stack, d, replaceEmpty: false);
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
            await UpdateRealmlistAddressAsync(stack, cancellationToken: cancellationToken);
            await RepushRegistrySafeAsync(cancellationToken);
            await RescanStackClientSafeAsync(stack.Id, cancellationToken);
        }

        // If the armory has "Load DBCs" enabled, make sure its DBC dataset is populated from the server.
        // We queue a detached background job (extract server DBCs -> CSV -> rebuild & restart armory) when
        // the flag was just turned on, or it is on but the dataset has no DBC CSVs yet. Subsequent saves
        // with an already-populated dataset are a no-op so we don't rebuild the image on every edit.
        MaybeQueueArmoryDbcSync(stack, oldArmoryLoadDbcs);

        return await MapAsync(stack, probeRuntime: true, cancellationToken);
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
            || string.IsNullOrWhiteSpace(deployment.ExternalSshUser))
        {
            throw new InvalidOperationException("Remote host and SSH user are required to reconnect.");
        }

        var privateKey = await ResolveReconnectPrivateKeyAsync(deployment, stack, cancellationToken);

        var test = await _remoteEngine.TestConnectionAsync(
            deployment.ExternalHost.Trim(),
            deployment.ExternalSshPort <= 0 ? 22 : deployment.ExternalSshPort,
            deployment.ExternalSshUser.Trim(),
            privateKey,
            cancellationToken: cancellationToken);
        if (!test.Success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(test.Message)
                ? "Remote connection test failed."
                : test.Message);
        }

        var previousExternalHost = RealmlistHostResolver.NormalizeHost(stack.ExternalHost);
        var previousResolvedHost = string.IsNullOrWhiteSpace(previousExternalHost)
            ? string.Empty
            : RealmlistHostResolver.ResolveForRealmAddress(previousExternalHost, cancellationToken);
        var overrideHost = RealmlistHostResolver.NormalizeHost(stack.RealmlistHostOverride);

        stack.ExternalHost = RealmlistHostResolver.NormalizeHost(deployment.ExternalHost);
        stack.ExternalSshPort = deployment.ExternalSshPort <= 0 ? 22 : deployment.ExternalSshPort;
        stack.ExternalSshUser = deployment.ExternalSshUser.Trim();
        if (!string.IsNullOrWhiteSpace(deployment.ExternalSshPrivateKey)
            || !string.IsNullOrWhiteSpace(deployment.SavedSshKeyId))
        {
            stack.ExternalSshPrivateKey = _secretProtector.Protect(privateKey);
        }

        ApplyCloudBinding(stack, deployment, replaceEmpty: false);

        if (string.IsNullOrWhiteSpace(overrideHost)
            || string.Equals(overrideHost, previousExternalHost, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(previousResolvedHost)
                && string.Equals(overrideHost, previousResolvedHost, StringComparison.OrdinalIgnoreCase)))
        {
            stack.RealmlistHostOverride = RealmlistHostResolver.ResolveForRealmAddress(
                stack.ExternalHost,
                cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
        await UpdateRealmlistAddressAsync(stack, throwOnFailure: false, cancellationToken);
        _logger.LogInformation("Reconnected external stack {StackId} to {Host}.", stackId, stack.ExternalHost);
        return await MapAsync(stack, probeRuntime: true, cancellationToken);
    }

    public async Task<SetRealmAddressResponseDto> BeginApplyStackPublicHostAsync(
        string stackId, string host, CancellationToken cancellationToken = default)
    {
        await PersistStackPublicHostAsync(stackId, host, cancellationToken);

        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleAsync(s => s.Id == stackId, cancellationToken);

        var snapshot = await CaptureServiceSnapshotAsync(stackId, cancellationToken);
        var job = _stackJobService.Enqueue(
            stackId,
            StackJobAction.ApplyPublicHost,
            new PublicHostApplyPlanDto
            {
                WasFullyStopped = snapshot.WasFullyStopped,
                ClientEnabled = stack.ClientEnabled,
                ArmoryEnabled = stack.ArmoryEnabled,
                DatabaseRunning = snapshot.Database,
                AuthRunning = snapshot.Auth,
                WorldRunning = snapshot.World,
                ClientRunning = snapshot.Client,
                ArmoryRunning = snapshot.Armory,
                ArmoryAssetsRunning = snapshot.ArmoryAssets,
            });
        return new SetRealmAddressResponseDto
        {
            Host = ResolveRealmlistHost(stack),
            Job = job,
        };
    }

    public async Task ApplyStackPublicHostLiveAsync(
        string stackId,
        Action<PublicHostApplyStepDto>? reportStep,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack '{stackId}' was not found.");

        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        if (!Directory.Exists(repoPath))
        {
            throw new InvalidOperationException("Stack has not been built yet.");
        }

        var snapshot = await CaptureServiceSnapshotAsync(stackId, cancellationToken);
        var startedDatabase = false;
        var startedClient = false;
        var restoreCompleted = false;
        string? activeStepId = null;
        string? activeStepLabel = null;

        void Report(string id, string label, PublicHostApplyStepStatus status, string? detail = null) =>
            reportStep?.Invoke(new PublicHostApplyStepDto
            {
                Id = id,
                Label = label,
                Status = status,
                Detail = detail,
            });

        void BeginStep(string id, string label)
        {
            activeStepId = id;
            activeStepLabel = label;
            Report(id, label, PublicHostApplyStepStatus.Running);
        }

        void CompleteStep(string id, string label, string? detail = null)
        {
            activeStepId = null;
            activeStepLabel = null;
            Report(id, label, PublicHostApplyStepStatus.Completed, detail);
        }

        void SkipStep(string id, string label, string? detail = null)
        {
            if (activeStepId == id)
            {
                activeStepId = null;
                activeStepLabel = null;
            }

            Report(id, label, PublicHostApplyStepStatus.Skipped, detail);
        }

        async Task RestoreTemporaryServicesAsync(bool throwOnFailure)
        {
            if (restoreCompleted || !snapshot.WasFullyStopped || (!startedDatabase && !startedClient))
            {
                return;
            }

            BeginStep("restore", "Restore previous stack state");
            try
            {
                if (startedClient)
                {
                    await RunDockerComposeAsync(stackId, "stop client", repoPath, cancellationToken);
                }

                if (startedDatabase)
                {
                    await RunDockerComposeAsync(stackId, "stop ac-database", repoPath, cancellationToken);
                }

                stack.Status = StackStatus.Stopped;
                await _dbContext.SaveChangesAsync(cancellationToken);
                CompleteStep("restore", "Restore previous stack state");
                restoreCompleted = true;
            }
            catch (Exception restoreEx)
            {
                _logger.LogWarning(
                    restoreEx,
                    "Failed restoring stack state after public host apply for stack {StackId}.",
                    stackId);
                Report("restore", "Restore previous stack state", PublicHostApplyStepStatus.Failed, restoreEx.Message);
                activeStepId = null;
                activeStepLabel = null;
                if (throwOnFailure)
                {
                    throw;
                }
            }
        }

        try
        {
            if (!snapshot.Database)
            {
                BeginStep("database", "Start database");
                await RunDockerComposeAsync(stackId, "up -d ac-database", repoPath, cancellationToken);
                await WaitForDatabaseServiceAsync(stackId, cancellationToken);
                await WaitForDatabaseReadyAsync(stack, stackId, cancellationToken);
                startedDatabase = true;
                CompleteStep("database", "Start database");
            }
            else
            {
                SkipStep("database", "Start database", "Database already running.");
            }

            BeginStep("realmlist", "Update realmlist in MySQL");
            await UpdateRealmlistInDatabaseAsync(stack, cancellationToken);
            CompleteStep("realmlist", "Update realmlist in MySQL");

            if (stack.ClientEnabled)
            {
                if (!snapshot.Client)
                {
                    BeginStep("client", "Start client server");
                    await RunDockerComposeAsync(stackId, "up -d client", repoPath, cancellationToken);
                    startedClient = true;
                    CompleteStep("client", "Start client server");
                }
                else
                {
                    SkipStep("client", "Start client server", "Client already running.");
                }

                BeginStep("registry", "Update launcher registry");
                await RepushRegistrySafeAsync(cancellationToken);
                CompleteStep("registry", "Update launcher registry");

                BeginStep("rescan", "Refresh client manifest");
                await RescanStackClientSafeAsync(stackId, cancellationToken);
                CompleteStep("rescan", "Refresh client manifest");
            }

            if (!snapshot.WasFullyStopped)
            {
                await RecreatePublicHostServicesAsync(
                    stack,
                    repoPath,
                    snapshot,
                    BeginStep,
                    (id, label) => CompleteStep(id, label),
                    SkipStep,
                    cancellationToken);
            }
            else
            {
                SkipRecreateStepsWhenFullyStopped(stack, snapshot, SkipStep);
            }

            if (snapshot.WasFullyStopped && (startedDatabase || startedClient))
            {
                await RestoreTemporaryServicesAsync(throwOnFailure: true);
            }
            else if (snapshot.WasFullyStopped)
            {
                SkipStep("restore", "Restore previous stack state", "Nothing needed.");
                restoreCompleted = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed applying public host for stack {StackId}.", stackId);
            if (activeStepId is not null && activeStepLabel is not null)
            {
                Report(activeStepId, activeStepLabel, PublicHostApplyStepStatus.Failed, ex.Message);
            }

            await RestoreTemporaryServicesAsync(throwOnFailure: false);
            throw;
        }
    }

    private async Task PersistStackPublicHostAsync(string stackId, string host, CancellationToken cancellationToken)
    {
        host = RealmlistHostResolver.NormalizeHost(host);
        if (host.Length is < 1 or > 255)
        {
            throw new ArgumentException("Stack public host must be between 1 and 255 characters.", nameof(host));
        }

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack '{stackId}' was not found.");

        EnsureStackLifecycleAllowed(stack, "update the public host of");

        stack.RealmlistHostOverride = host;
        if (System.Net.IPAddress.TryParse(host, out var parsedHost))
        {
            // Realmlist address is what players dial; Docker publish bind is which local NIC listens.
            // On cloud VPCs the public/elastic IP is NAT-mapped and cannot be bound (EADDRNOTAVAIL).
            if (stack.DeploymentTarget == DeploymentTarget.External)
            {
                if (string.Equals(stack.PublishBindAddress, host, StringComparison.OrdinalIgnoreCase)
                    || (!RealmlistHostResolver.IsPrivateOrNonRoutableIpv4Literal(host)
                        && string.Equals(stack.PublishBindAddress, parsedHost.ToString(), StringComparison.OrdinalIgnoreCase)))
                {
                    stack.PublishBindAddress = string.Empty;
                }
            }
            else if (IsDockerPublishBindAddress(parsedHost))
            {
                stack.PublishBindAddress = parsedHost.ToString();
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        if (Directory.Exists(repoPath))
        {
            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken);
        }
    }

    private sealed record ServiceSnapshot(
        bool Database,
        bool Auth,
        bool World,
        bool Client,
        bool Armory,
        bool ArmoryAssets)
    {
        public bool WasFullyStopped => !Database && !Auth && !World && !Client && !Armory && !ArmoryAssets;
    }

    private async Task<ServiceSnapshot> CaptureServiceSnapshotAsync(string stackId, CancellationToken cancellationToken)
    {
        var containers = await GetContainersAsync(stackId, cancellationToken);
        static bool Running(IEnumerable<ContainerStatusDto> list, Func<ContainerStatusDto, bool> match) =>
            list.Any(c => match(c) && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase));

        return new ServiceSnapshot(
            Database: Running(containers, c => c.Name.Contains("database", StringComparison.OrdinalIgnoreCase)),
            Auth: Running(containers, c => c.Name.Contains("authserver", StringComparison.OrdinalIgnoreCase)),
            World: Running(containers, c => c.Name.Contains("worldserver", StringComparison.OrdinalIgnoreCase)),
            Client: Running(containers, c => c.Name.EndsWith("-client", StringComparison.OrdinalIgnoreCase)),
            Armory: Running(containers, c => c.Name.EndsWith("-armory", StringComparison.OrdinalIgnoreCase)),
            ArmoryAssets: Running(containers, c => c.Name.EndsWith("-armory-assets", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task UpdateRealmlistInDatabaseAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        var host = ResolveRealmlistHost(stack);
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("No realmlist host configured for this stack.");
        }

        var realmAddress = RealmlistHostResolver.ResolveForRealmAddress(host, cancellationToken);
        if (!string.Equals(host, realmAddress, StringComparison.OrdinalIgnoreCase)
            && string.Equals(stack.RealmlistHostOverride, host, StringComparison.OrdinalIgnoreCase))
        {
            stack.RealmlistHostOverride = realmAddress;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await ExecuteRealmlistUpdateViaDockerAsync(stack, realmAddress, throwOnFailure: true, cancellationToken);
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

    private static void SkipRecreateStepsWhenFullyStopped(
        ManagedStackEntity stack,
        ServiceSnapshot snapshot,
        Action<string, string, string?> skipStep)
    {
        const string reason = "Stack was stopped — service was not running.";
        skipStep("recreate-auth", "Recreate auth server", reason);
        skipStep("recreate-world", "Recreate world server", reason);
        if (stack.ArmoryEnabled)
        {
            skipStep("recreate-armory", "Recreate armory", reason);
            skipStep("recreate-armory-assets", "Recreate armory assets", reason);
        }

        if (stack.ClientEnabled)
        {
            skipStep("recreate-client", "Recreate client server", reason);
        }
    }

    private async Task RecreatePublicHostServicesAsync(
        ManagedStackEntity stack,
        string repoPath,
        ServiceSnapshot snapshot,
        Action<string, string> beginStep,
        Action<string, string> completeStep,
        Action<string, string, string?> skipStep,
        CancellationToken cancellationToken)
    {
        var recreatePlans = new List<(string StepId, string Label, string ComposeService, bool WasRunning)>
        {
            ("recreate-auth", "Recreate auth server", "ac-authserver", snapshot.Auth),
            ("recreate-world", "Recreate world server", "ac-worldserver", snapshot.World),
        };

        if (stack.ArmoryEnabled)
        {
            recreatePlans.Add(("recreate-armory", "Recreate armory", "frontend-armory", snapshot.Armory));
            recreatePlans.Add(("recreate-armory-assets", "Recreate armory assets", "armory-assets", snapshot.ArmoryAssets));
        }

        if (stack.ClientEnabled)
        {
            recreatePlans.Add(("recreate-client", "Recreate client server", "client", snapshot.Client));
        }

        var servicesToRecreate = new List<string>();
        foreach (var (stepId, label, composeService, wasRunning) in recreatePlans)
        {
            if (!wasRunning)
            {
                skipStep(stepId, label, "Service was not running.");
                continue;
            }

            servicesToRecreate.Add(composeService);
        }

        if (servicesToRecreate.Count == 0)
        {
            return;
        }

        foreach (var (stepId, label, composeService, wasRunning) in recreatePlans)
        {
            if (!wasRunning)
            {
                continue;
            }

            beginStep(stepId, label);
        }

        await RunDockerComposeAsync(
            stack.Id,
            $"up -d --force-recreate --no-deps {string.Join(' ', servicesToRecreate.Distinct())}",
            repoPath,
            cancellationToken);

        foreach (var (stepId, label, _, wasRunning) in recreatePlans)
        {
            if (wasRunning)
            {
                completeStep(stepId, label);
            }
        }
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

    public async Task<bool> DeleteAsync(
        string stackId,
        bool terminateCloudInstance = false,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        var isExternal = stack.DeploymentTarget == DeploymentTarget.External;
        if (terminateCloudInstance)
        {
            if (!isExternal)
            {
                throw new InvalidOperationException("Only external VPC stacks can terminate a cloud VM.");
            }

            await _cloudInstanceLifecycle.TerminateStackInstanceAsync(
                new ManagedStackCloudTarget
                {
                    StackId = stack.Id,
                    StackName = stack.StackName,
                    PublicHost = stack.ExternalHost,
                    CloudConnectionId = stack.CloudConnectionId,
                    CloudInstanceId = stack.CloudInstanceId,
                    CloudRegion = stack.CloudRegion,
                },
                cancellationToken);
        }

        var stackPath = GetStackPath(stackId);
        var repoPath = Path.Combine(stackPath, "azerothcore-wotlk");

        if (isExternal)
        {
            // VPC / external stacks: detach from the manager only. Containers, volumes, and images on
            // the remote host are left untouched — operators manage those on the VPS directly.
            _logger.LogInformation(
                "Removing external stack {StackId} from the manager (remote Docker resources are not stopped).",
                stackId);

            try
            {
                await _remoteEngine.RemoveContextAsync(stack, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove remote docker context for external stack {StackId}", stack.Id);
            }
        }
        else
        {
            try
            {
                if (Directory.Exists(repoPath))
                {
                    await RunDockerComposeAsync(stackId, "down -v", repoPath, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "compose down failed during delete of local stack {StackId}", stackId);
            }

            await CleanupStackDockerFootprintAsync(stack, cancellationToken);
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

        StackListStatusCaches.TryRemove(stackId, out _);
        ExternalRuntimeProbeCaches.TryRemove(stackId, out _);

        _ = Task.Run(() => RepushRegistrySafeAsync(CancellationToken.None));
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
            ReportLifecycleProgress(stackId, "Preparing stack images…");

            // Start the armory alongside the stack. Best-effort: if the image can't be built we
            // simply omit it from the compose so the game servers still start.
            var armoryReady = await TryEnsureArmoryImageAsync(stack.Id, cancellationToken);
            stack.ArmoryEnabled = armoryReady;

            // Same for the per-stack client-server: build/ensure the shared image (on the stack's
            // engine) and only render it into the compose when it's actually available.
            var clientReady = stack.ClientEnabled && await TryEnsureClientImageAsync(stack, cancellationToken);

            ReportLifecycleProgress(stackId, "Updating runtime configuration…");
            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken, includeArmory: armoryReady, includeClient: clientReady);

            if (stack.DeploymentTarget == DeploymentTarget.External)
            {
                ReportLifecycleProgress(
                    stackId,
                    "Checking remote engine images (large images are only transferred once)…");
            }

            await ShipExternalStackImagesAsync(stack, armoryReady, clientReady, cancellationToken);
            await BringStackUpAsync(stack, stackId, repoPath, armoryReady, clientReady, cancellationToken);

            ReportLifecycleProgress(stackId, "Waiting for game servers to become healthy…");
            await WaitForRunningServicesAsync(stackId, cancellationToken);
            await UpdateRealmlistAddressAsync(stack, cancellationToken: cancellationToken);

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

    public async Task<bool> StopAsync(string stackId, CancellationToken cancellationToken = default) =>
        await ForceStopAsync(stackId, cancellationToken);

    public async Task<bool> ForceStopAsync(string stackId, CancellationToken cancellationToken = default)
    {
        // Detach from the HTTP/job caller so shutdown completes even if they navigate away.
        cancellationToken = CancellationToken.None;

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
            ReportLifecycleProgress(stackId, "Stopping stack containers…");

            // Stop crash-looping services first (`restart: unless-stopped` keeps respawning until explicitly stopped).
            await RunDockerComposeAsync(
                stackId,
                "stop -t 5 ac-authserver ac-worldserver frontend-armory armory-assets client",
                repoPath,
                cancellationToken,
                throwOnError: false);

            // Compose can fail silently on external stacks (SSH/context hiccups); force-remove by the
            // pinned container_name values we always generate for this stack.
            await ForceRemoveAllStackContainersByNameAsync(stackId, stack, cancellationToken);

            await RunDockerComposeAsync(
                stackId,
                "down --timeout 10 --remove-orphans",
                repoPath,
                cancellationToken,
                throwOnError: false);

            await ForceRemoveAllStackContainersByNameAsync(stackId, stack, cancellationToken);
            await WaitForStackToStopAsync(stackId, stack, cancellationToken);
            await RemoveRemainingStackContainersAsync(stackId, stack, cancellationToken);

            stack.ArmoryEnabled = false;
            stack.Status = StackStatus.Stopped;
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (stack.DeploymentTarget == DeploymentTarget.External)
            {
                CacheExternalRuntimeProbe(
                    stackId,
                    [],
                    BuildServiceList([]),
                    StackStatus.Stopped,
                    true,
                    null);
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
            await UpdateRealmlistAddressAsync(stack, cancellationToken: cancellationToken);

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
        await UpdateRealmlistAddressAsync(stack, throwOnFailure: true, cancellationToken);
        await RunDockerComposeAsync(
            stackId,
            "up -d --force-recreate --no-deps ac-worldserver ac-authserver",
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
        var resolvedPublishBind = ResolvePublishBindIp(stack);
        if (resolvedPublishBind is not null)
        {
            effectiveBind = resolvedPublishBind;
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
        await _dbContext.SaveChangesAsync(cancellationToken);
        await EnsureRuntimeConfigurationAsync(
            stack,
            repoPath,
            cancellationToken,
            includeArmory: true,
            includeClient: stack.ClientEnabled);
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
            await WaitForDatabaseReadyAsync(stack, stackId, cancellationToken);
        }

        await _armoryDatabase.EnsureProvisionedAsync(stackId, cancellationToken);

        var recreate = forceRecreate ? " --force-recreate" : string.Empty;
        if (forceRecreate)
        {
            await PrepareFixedNameServiceRecreateAsync(stackId, stack, "frontend-armory", repoPath, cancellationToken);
        }

        var armoryOptions = BuildArmoryComposeOptions(stack);
        var armoryServices = new List<string> { "frontend-armory" };
        if (armoryOptions.AssetsAvailable)
        {
            armoryServices.Add("armory-assets");
        }

        await RunDockerComposeAsync(
            stackId,
            $"up -d --no-deps{recreate} {string.Join(' ', armoryServices)}",
            repoPath,
            cancellationToken);

        try
        {
            await _armoryImageService.SyncLiveLayoutAsync(stackId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync live armory layout after starting armory for stack {StackId}.", stackId);
        }

        await TrySyncExternalWebFirewallAsync(stack, cancellationToken);

        return true;
    }

    /// <summary>
    /// Brings up the per-stack client file-server container. Mirrors <see cref="StartArmoryInternalAsync"/>.
    /// </summary>
    public async Task<bool> StartClientAsync(string stackId, bool forceRecreate = false, CancellationToken cancellationToken = default)
    {
        cancellationToken = CancellationToken.None;

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        EnsureStackLifecycleAllowed(stack, "start the client files service of");

        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        if (!Directory.Exists(repoPath))
        {
            throw new InvalidOperationException("Stack has not been built yet.");
        }

        if (forceRecreate)
        {
            await _clientServerImageService.RebuildImageAsync(dockerContext: null, cancellationToken);
        }
        else
        {
            await _clientServerImageService.EnsureImageAsync(dockerContext: null, cancellationToken);
        }

        stack.ClientEnabled = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await EnsureRuntimeConfigurationAsync(
            stack,
            repoPath,
            cancellationToken,
            includeArmory: stack.ArmoryEnabled,
            includeClient: true);
        await ShipExternalStackImagesAsync(stack, includeArmory: false, includeClient: true, cancellationToken);

        if (forceRecreate)
        {
            await PrepareFixedNameServiceRecreateAsync(stackId, stack, "client", repoPath, cancellationToken);
        }

        var recreate = forceRecreate ? " --force-recreate" : string.Empty;
        await RunDockerComposeAsync(
            stackId,
            $"up -d --no-deps{recreate} client",
            repoPath,
            cancellationToken);

        await RepushRegistrySafeAsync(cancellationToken);
        return true;
    }

    public async Task<bool> StopClientAsync(string stackId, CancellationToken cancellationToken = default)
    {
        cancellationToken = CancellationToken.None;

        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

        if (stack is null)
        {
            return false;
        }

        var repoPath = Path.Combine(GetStackPath(stackId), "azerothcore-wotlk");
        stack.ClientEnabled = false;
        if (Directory.Exists(repoPath))
        {
            await RunDockerComposeAsync(stackId, "rm -sf client", repoPath, cancellationToken, throwOnError: false);
            await EnsureRuntimeConfigurationAsync(
                stack,
                repoPath,
                cancellationToken,
                includeArmory: stack.ArmoryEnabled,
                includeClient: false);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RestartClientAsync(string stackId, CancellationToken cancellationToken = default)
    {
        cancellationToken = CancellationToken.None;

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

        stack.ClientEnabled = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await EnsureRuntimeConfigurationAsync(
            stack,
            repoPath,
            cancellationToken,
            includeArmory: stack.ArmoryEnabled,
            includeClient: true);
        await RunDockerComposeAsync(stackId, "restart client", repoPath, cancellationToken);
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
            await RunDockerComposeAsync(stackId, "rm -sf frontend-armory armory-assets", repoPath, cancellationToken, throwOnError: false);
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
            if (armoryReady)
            {
                await WaitForDatabaseServiceAsync(stackId, cancellationToken);
                await _armoryDatabase.EnsureProvisionedAsync(stackId, cancellationToken);
            }

            return;
        }

        var containerPrefix = DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName);

        // Stop game servers first so crash-looping auth/world processes cannot hammer MySQL while the
        // database is starting or db-import is running.
        ReportLifecycleProgress(stackId, "Stopping game servers before database startup…");
        await RunDockerComposeAsync(
            stackId,
            "stop ac-authserver ac-worldserver frontend-armory armory-assets client",
            repoPath,
            cancellationToken,
            throwOnError: false);

        ReportLifecycleProgress(stackId, "Starting database on the VPC…");
        await RunDockerComposeAsync(stackId, "up -d ac-database", repoPath, cancellationToken);
        await WaitForDatabaseServiceAsync(stackId, cancellationToken);

        var dbImportName = $"{containerPrefix}-db-import";
        var clientDataName = $"{containerPrefix}-client-data-init";
        var dbImportDone = await IsDbImportCompleteAsync(stack, stackId, dbImportName, cancellationToken);
        var clientDataDone = await IsClientDataInitCompleteAsync(stack, stackId, clientDataName, cancellationToken);

        var initServices = new List<string>();
        if (!dbImportDone)
        {
            initServices.Add("ac-db-import");
        }

        if (!clientDataDone)
        {
            initServices.Add("ac-client-data-init");
        }

        if (initServices.Count > 0)
        {
            ReportLifecycleProgress(
                stackId,
                initServices.Count == 2
                    ? "Running first-time database and client-data setup (one-time; can take several minutes)…"
                    : $"Running setup: {string.Join(", ", initServices)}…");
            await RunDockerComposeAsync(stackId, $"up -d {string.Join(' ', initServices)}", repoPath, cancellationToken);
            if (!dbImportDone)
            {
                await WaitForInitContainerAsync(stackId, dbImportName, "DB import", cancellationToken);
            }

            if (!clientDataDone)
            {
                await WaitForInitContainerAsync(stackId, clientDataName, "Client data init", cancellationToken);
            }
        }
        else
        {
            _logger.LogInformation(
                "Skipping init containers for stack {StackId} — db-import and client-data-init already completed.",
                stackId);
        }

        // db-import hammers MySQL; wait until it accepts connections again before game servers start.
        ReportLifecycleProgress(stackId, "Waiting for MySQL to accept connections…");
        await WaitForDatabaseReadyAsync(stack, stackId, cancellationToken);

        if (armoryReady)
        {
            ReportLifecycleProgress(stackId, "Provisioning armory database…");
            await _armoryDatabase.EnsureProvisionedAsync(stackId, cancellationToken);
            await EnsureRuntimeConfigurationAsync(stack, repoPath, cancellationToken, includeArmory: true, includeClient: clientReady);
        }

        // Auth validates that realmlist.address resolves from inside its container — set the row before
        // auth/world start, and store a literal IP (not an EC2 hostname Docker DNS cannot resolve).
        ReportLifecycleProgress(stackId, "Updating realm address in MySQL…");
        await UpdateRealmlistAddressAsync(stack, throwOnFailure: true, cancellationToken);

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

        ReportLifecycleProgress(
            stackId,
            $"Starting {services.Count} service container(s) on the VPC…");
        // `--no-deps` is critical: the base compose requires ac-db-import to have completed, and after
        // `compose down` that one-shot container is gone — compose would otherwise re-run db-import while
        // auth/world start, hammering MySQL and causing "Lost connection to MySQL server" errors.
        await RunDockerComposeAsync(
            stackId,
            $"up -d --no-deps {string.Join(' ', services)}",
            repoPath,
            cancellationToken);
    }

    /// <summary>True when a one-shot init container exists and exited successfully.</summary>
    private async Task<bool> IsInitContainerCompleteAsync(
        string stackId,
        string containerName,
        CancellationToken cancellationToken)
    {
        var contextArg = await GetDockerContextArgAsync(stackId, cancellationToken);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"{contextArg}inspect -f \"{{{{.State.Status}}}}|{{{{.State.ExitCode}}}}\" {containerName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null)
        {
            return false;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            return false;
        }

        var parts = stdout.Trim().Split('|');
        var status = parts.Length > 0 ? parts[0].Trim() : string.Empty;
        if (!status.Equals("exited", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts.Length > 1
               && int.TryParse(parts[1].Trim(), out var exitCode)
               && exitCode == 0;
    }

    /// <summary>
    /// True when db-import finished. After <c>compose down</c> the one-shot container is removed but the
    /// MySQL volume retains the imported schema — fall back to checking that before re-running import.
    /// </summary>
    private async Task<bool> IsDbImportCompleteAsync(
        ManagedStackEntity stack,
        string stackId,
        string dbImportContainerName,
        CancellationToken cancellationToken)
    {
        if (await IsInitContainerCompleteAsync(stackId, dbImportContainerName, cancellationToken))
        {
            return true;
        }

        if (!await IsAzerothCoreDatabasePopulatedAsync(stack, stackId, cancellationToken))
        {
            return false;
        }

        // World schema can exist while acore_auth.realmlist is still empty (skipped import, manual DB
        // work, or starting auth before a full stack start). Treat as incomplete so db-import runs once.
        return await IsRealmlistPopulatedAsync(stack, stackId, cancellationToken);
    }

    /// <summary>
    /// True when client-data-init finished. After <c>compose down</c> the container is removed but the
    /// client-data volume retains downloaded DBCs.
    /// </summary>
    private async Task<bool> IsClientDataInitCompleteAsync(
        ManagedStackEntity stack,
        string stackId,
        string clientDataContainerName,
        CancellationToken cancellationToken)
    {
        if (await IsInitContainerCompleteAsync(stackId, clientDataContainerName, cancellationToken))
        {
            return true;
        }

        var clientDataVolume = $"{DockerComposeOverrideGenerator.GetComposeProjectName(stackId)}_ac-client-data";
        var dbcCount = await _remoteEngine.CountVolumeFilesAsync(
            stack,
            clientDataVolume,
            "dbc",
            "*.dbc",
            cancellationToken);
        return dbcCount > 0;
    }

    private async Task<bool> IsAzerothCoreDatabasePopulatedAsync(
        ManagedStackEntity stack,
        string stackId,
        CancellationToken cancellationToken)
    {
        var containerPrefix = DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName);
        var containerName = $"{containerPrefix}-database";
        var contextArg = await GetDockerContextArgAsync(stackId, cancellationToken);
        var arguments =
            $"{contextArg}exec {containerName} mysql -N -uroot " +
            $"-p{stack.DatabaseRootPassword} -e " +
            "\"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='acore_world' AND table_name='version';\"";

        var (exitCode, stdout, _) = await RunDockerCliAsync(arguments, cancellationToken);
        if (exitCode != 0)
        {
            return false;
        }

        return int.TryParse(stdout.Trim(), out var tableCount) && tableCount > 0;
    }

    private async Task<bool> IsRealmlistPopulatedAsync(
        ManagedStackEntity stack,
        string stackId,
        CancellationToken cancellationToken)
    {
        var containerPrefix = DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName);
        var containerName = $"{containerPrefix}-database";
        var contextArg = await GetDockerContextArgAsync(stackId, cancellationToken);
        var arguments =
            $"{contextArg}exec {containerName} mysql -N -uroot " +
            $"-p{stack.DatabaseRootPassword} -e " +
            "\"SELECT COUNT(*) FROM acore_auth.realmlist;\"";

        var (exitCode, stdout, _) = await RunDockerCliAsync(arguments, cancellationToken);
        if (exitCode != 0)
        {
            return false;
        }

        return int.TryParse(stdout.Trim(), out var rowCount) && rowCount > 0;
    }

    private void ReportLifecycleProgress(string stackId, string message) =>
        _stackJobService.ReportProgress(stackId, message);

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

    private async Task RunDockerComposeAsync(
        string stackId,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        bool throwOnError = true)
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

        if (throwOnError && process.ExitCode != 0)
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

    private string ResolveRealmlistHost(ManagedStackEntity stack)
    {
        var host = RealmlistHostResolver.NormalizeHost(string.IsNullOrWhiteSpace(stack.RealmlistHostOverride)
            ? string.Empty
            : stack.RealmlistHostOverride);

        if (string.IsNullOrWhiteSpace(host))
        {
            host = RealmlistHostResolver.NormalizeHost(_migrationOptions.RealmlistHost);
        }

        if (string.IsNullOrWhiteSpace(host) && stack.DeploymentTarget == DeploymentTarget.External)
        {
            host = RealmlistHostResolver.NormalizeHost(stack.ExternalHost);
        }

        return host;
    }

    /// <summary>
    /// Idempotently rewrites the acore_auth.realmlist row (id 1) so the auth server hands connecting
    /// clients the correct world address/port. Without this the upstream db-import default of
    /// 127.0.0.1:8085 is served and non-local clients cannot connect even after a successful login.
    /// Best-effort unless <paramref name="throwOnFailure"/> is true (used before auth/world start).
    /// </summary>
    private async Task UpdateRealmlistAddressAsync(
        ManagedStackEntity stack,
        bool throwOnFailure = false,
        CancellationToken cancellationToken = default)
    {
        var host = ResolveRealmlistHost(stack);
        if (string.IsNullOrWhiteSpace(host))
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException(
                    "No realmlist host configured for this stack. Set the realm address on the Realms tab " +
                    "or update the VPC connection for external stacks.");
            }

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
            await ExecuteRealmlistUpdateViaDockerAsync(stack, realmAddress, throwOnFailure, cancellationToken);
        }
        catch (Exception ex)
        {
            if (throwOnFailure)
            {
                throw;
            }

            _logger.LogWarning(ex, "Failed to update acore_auth.realmlist for stack {StackId}.", stack.Id);
        }
    }

    /// <summary>
    /// Updates acore_auth.realmlist via <c>docker exec … mysql</c> so external stacks do not depend on
    /// the SSH management tunnel reaching the published MySQL port on the remote host.
    /// </summary>
    private async Task ExecuteRealmlistUpdateViaDockerAsync(
        ManagedStackEntity stack,
        string realmAddress,
        bool throwOnFailure,
        CancellationToken cancellationToken)
    {
        var composeProjectName = DockerComposeOverrideGenerator.GetComposeProjectName(stack.Id);
        var dockerContext = await ResolveDockerContextAsync(stack, cancellationToken);
        var containers = await _dockerService.ListContainersAsync(
            composeProjectName,
            dockerContext,
            cancellationToken: cancellationToken);
        var databaseContainer = containers
            .FirstOrDefault(c => c.Name.Contains("database", StringComparison.OrdinalIgnoreCase));

        if (databaseContainer is null)
        {
            var message = "Database container not found.";
            if (throwOnFailure)
            {
                throw new InvalidOperationException(message);
            }

            _logger.LogWarning("Database container not found for stack {StackId}; skipping realmlist update.", stack.Id);
            return;
        }

        var realmName = string.IsNullOrWhiteSpace(stack.RealmName) ? "AzerothCore" : stack.RealmName;
        var sql =
            "INSERT INTO acore_auth.realmlist " +
            "(id, name, address, localAddress, localSubnetMask, port, icon, flag, timezone, allowedSecurityLevel, population, gamebuild) " +
            $"VALUES (1, '{EscapeSqlLiteral(realmName)}', '{EscapeSqlLiteral(realmAddress)}', " +
            $"'{EscapeSqlLiteral(realmAddress)}', '255.255.255.0', {stack.WorldServerPort}, 0, 0, 1, 0, 0, 12340) " +
            "ON DUPLICATE KEY UPDATE " +
            $"name='{EscapeSqlLiteral(realmName)}', " +
            $"address='{EscapeSqlLiteral(realmAddress)}', " +
            $"localAddress='{EscapeSqlLiteral(realmAddress)}', " +
            "localSubnetMask='255.255.255.0', " +
            $"port={stack.WorldServerPort}, " +
            "flag=0;";

        var contextArg = await GetDockerContextArgAsync(stack.Id, cancellationToken);
        var arguments =
            $"{contextArg}exec -i {databaseContainer.Name} mysql -uroot " +
            $"-p{stack.DatabaseRootPassword} -e \"{sql.Replace("\"", "\\\"")}\"";

        var (exitCode, _, error) = await RunDockerCliAsync(arguments, cancellationToken);
        if (exitCode != 0)
        {
            var actualError = string.Join("\n", (error ?? string.Empty)
                .Split('\n')
                .Where(line => !line.Contains("Using a password on the command line", StringComparison.OrdinalIgnoreCase)))
                .Trim();
            if (string.IsNullOrWhiteSpace(actualError))
            {
                actualError = $"mysql exited with code {exitCode}.";
            }

            if (throwOnFailure)
            {
                throw new InvalidOperationException($"Failed updating realmlist in MySQL: {actualError}");
            }

            _logger.LogWarning(
                "Realmlist update for stack {StackId} exited {Exit}: {Error}",
                stack.Id,
                exitCode,
                actualError);
            return;
        }

        _logger.LogInformation(
            "Realmlist for stack {StackId} set to {Host}:{Port} ({Realm}).",
            stack.Id,
            realmAddress,
            stack.WorldServerPort,
            realmName);
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

    private async Task<StackDetailsDto> MapAsync(
        ManagedStackEntity stack,
        bool probeRuntime,
        CancellationToken cancellationToken,
        bool preferCachedRuntimeProbe = false)
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

        List<ContainerStatusDto> containers;
        List<StackServiceDto> services;
        StackStatus runtimeStatus;
        var externalReconnect = EvaluateExternalReconnect(stack);
        bool? dockerEngineAvailable = null;
        string? dockerEngineUnavailableReason = null;

        // A lifecycle job hammers the remote Docker engine over SSH; skip the live probe so detail
        // refreshes stay fast and the UI can show job progress instead of hanging on docker ps.
        var activeJob = _stackJobService.GetStatus(stack.Id);
        var skipRuntimeProbe = activeJob is { IsRunning: true }
            || stack.Status == StackStatus.SetupIncomplete;

        if (probeRuntime && !skipRuntimeProbe)
        {
            RuntimeProbeResult probeResult;
            if (preferCachedRuntimeProbe
                && TryRestoreExternalRuntimeProbeCache(
                    stack.Id,
                    out var reusedContainers,
                    out var reusedServices,
                    out var reusedRuntimeStatus,
                    out var reusedEngineAvailable,
                    out var reusedEngineError))
            {
                probeResult = new RuntimeProbeResult(
                    reusedContainers,
                    reusedServices,
                    reusedRuntimeStatus,
                    reusedEngineAvailable,
                    reusedEngineError);
            }
            else if (ShouldServeCachedExternalProbeOnly(stack)
                && TryRestoreExternalRuntimeProbeCache(
                    stack.Id,
                    out var cachedContainers,
                    out var cachedServices,
                    out var cachedRuntimeStatus,
                    out var cachedEngineAvailable,
                    out var cachedEngineError)
                && cachedRuntimeStatus is StackStatus.Stopped or StackStatus.Failed)
            {
                probeResult = new RuntimeProbeResult(
                    cachedContainers,
                    cachedServices,
                    cachedRuntimeStatus,
                    cachedEngineAvailable,
                    cachedEngineError);

                if (!externalReconnect.NeedsReconnect)
                {
                    probeResult = await RefreshExternalEngineAvailabilityAsync(
                        stack,
                        probeResult,
                        cancellationToken);
                }
            }
            else if (stack.DeploymentTarget == DeploymentTarget.External)
            {
                probeResult = await WithExternalProbeLockAsync(
                    stack.Id,
                    () => ProbeRuntimeAsync(stack, externalReconnect, cancellationToken),
                    cancellationToken);
            }
            else
            {
                probeResult = await ProbeRuntimeAsync(stack, externalReconnect, cancellationToken);
            }

            containers = probeResult.Containers;
            services = probeResult.Services;
            runtimeStatus = probeResult.RuntimeStatus;
            dockerEngineAvailable = probeResult.EngineAvailable;
            dockerEngineUnavailableReason = probeResult.EngineUnavailableReason;
        }
        else
        {
            containers = [];
            services = BuildServiceList(containers);
            runtimeStatus = stack.Status;
        }

        // While a detached start/restart/start-database job is running, report Starting so both the list
        // and detail views reflect the in-progress operation (containers aren't up yet, so the raw
        // runtime status would otherwise read Stopped) and the Start button stays hidden/disabled.
        var job = _stackJobService.GetStatus(stack.Id);
        if (job is { IsRunning: true } && job.Action is not StackJobAction.Stop
            && runtimeStatus is not (StackStatus.Running or StackStatus.Building or StackStatus.Initializing))
        {
            runtimeStatus = StackStatus.Starting;
        }
        else if (job is { IsRunning: true, Action: StackJobAction.Stop })
        {
            runtimeStatus = StackStatus.Stopped;
        }
        else if (stack.Status == StackStatus.Failed
                 && runtimeStatus == StackStatus.Stopped
                 && job is not { IsRunning: true })
        {
            // A prior lifecycle job failed (often while the VPC was unreachable). Once containers are
            // actually down again — e.g. after a reboot — present Stopped so operators can restart.
            runtimeStatus = StackStatus.Stopped;
        }
        else if (!probeRuntime
                 && stack.Status == StackStatus.Failed
                 && job is not { IsRunning: true })
        {
            runtimeStatus = StackStatus.Stopped;
        }

        var armoryRunning = probeRuntime
            ? containers.Any(c =>
                c.Name.Contains("armory", StringComparison.OrdinalIgnoreCase)
                && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase))
            : false;

        var isAdminAccountInitialized = stack.IsAdminAccountInitialized;
        var adminAccountInitializedAt = stack.AdminAccountInitializedAt;
        var dbContainerRunning = containers.Any(c =>
            c.Name.Contains("database", StringComparison.OrdinalIgnoreCase)
            && c.Status.Contains("running", StringComparison.OrdinalIgnoreCase));
        var canAttemptSoapReconcile = dbContainerRunning
            || (stack.DeploymentTarget == DeploymentTarget.External && !externalReconnect.NeedsReconnect);

        if (probeRuntime && !skipRuntimeProbe && canAttemptSoapReconcile)
        {
            var shouldReconcileSoap = !LastSoapAdminReconcileAt.TryGetValue(stack.Id, out var lastReconcile)
                || DateTime.UtcNow - lastReconcile >= SoapAdminReconcileMinInterval;
            if (shouldReconcileSoap)
            {
                var reconciled = await ReconcileSoapAdminFlagAsync(stack, cancellationToken);
                isAdminAccountInitialized = reconciled.Initialized;
                adminAccountInitializedAt = reconciled.InitializedAt;
                LastSoapAdminReconcileAt[stack.Id] = DateTime.UtcNow;
            }
        }

        return new StackDetailsDto
        {
            StackId = stack.Id,
            StackName = stack.StackName,
            DisplayName = DisplayNameFor(stack),
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
                },
                Deployment = new DeploymentConfigDto
                {
                    Target = stack.DeploymentTarget,
                    ExternalHost = stack.ExternalHost,
                    ExternalSshPort = stack.ExternalSshPort == 0 ? 22 : stack.ExternalSshPort,
                    ExternalSshUser = stack.ExternalSshUser,
                    // Never return the private key material to clients.
                    ExternalSshPrivateKey = string.Empty,
                    CloudConnectionId = stack.CloudConnectionId,
                    CloudInstanceId = stack.CloudInstanceId,
                    CloudRegion = stack.CloudRegion,
                    CloudProvider = stack.CloudProvider,
                    CloudInstanceType = stack.CloudInstanceType,
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
            IsAdminAccountInitialized = isAdminAccountInitialized,
            AdminAccountInitializedAt = adminAccountInitializedAt,
            ArmoryPort = stack.ArmoryPort,
            ArmoryRunning = armoryRunning,
            ModulesPendingRebuild = GetModulesPendingRebuild(stack.Id, Deserialize<List<string>>(stack.ModuleIdsJson) ?? []),
            NeedsExternalReconnect = externalReconnect.NeedsReconnect,
            ExternalReconnectReason = externalReconnect.Reason,
            DockerEngineAvailable = dockerEngineAvailable,
            DockerEngineUnavailableReason = dockerEngineUnavailableReason,
            HasCompletedBuild = stack.LastBuiltAt.HasValue || !string.IsNullOrEmpty(stack.CoreCommitSha),
            WizardStepId = stack.Status == StackStatus.SetupIncomplete
                ? (string.IsNullOrWhiteSpace(stack.WizardStepId) ? "deployment" : stack.WizardStepId)
                : null,
            SshHardeningCompletedAt = stack.SshHardeningCompletedAt,
        };
    }

    private sealed record RuntimeProbeResult(
        List<ContainerStatusDto> Containers,
        List<StackServiceDto> Services,
        StackStatus RuntimeStatus,
        bool? EngineAvailable,
        string? EngineUnavailableReason);

    private static bool ShouldServeCachedExternalProbeOnly(ManagedStackEntity stack) =>
        stack.DeploymentTarget == DeploymentTarget.External
        && stack.Status is StackStatus.Stopped or StackStatus.Failed;

    /// <summary>
    /// Re-probes SSH/Docker reachability for stopped external stacks that reuse cached container
    /// status, so the list still reflects a powered-off cloud instance.
    /// </summary>
    private async Task<RuntimeProbeResult> RefreshExternalEngineAvailabilityAsync(
        ManagedStackEntity stack,
        RuntimeProbeResult cached,
        CancellationToken cancellationToken)
    {
        try
        {
            var (available, message) = await _remoteEngine.ProbeRemoteDockerAsync(stack, cancellationToken);
            return cached with
            {
                EngineAvailable = available,
                EngineUnavailableReason = available ? null : message,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh VPC engine availability for stack {StackId}.", stack.Id);
            return cached with
            {
                EngineAvailable = false,
                EngineUnavailableReason = ex.Message,
            };
        }
    }

    private async Task<RuntimeProbeResult> ProbeRuntimeAsync(
        ManagedStackEntity stack,
        (bool NeedsReconnect, string? Reason) externalReconnect,
        CancellationToken cancellationToken)
    {
        List<ContainerStatusDto> containers;
        List<StackServiceDto> services;
        StackStatus runtimeStatus;
        bool? dockerEngineAvailable;
        string? dockerEngineUnavailableReason;

        string? dockerContext = null;
        using var probeTimeoutCts = stack.DeploymentTarget == DeploymentTarget.External
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (probeTimeoutCts is not null)
        {
            probeTimeoutCts.CancelAfter(ExternalRuntimeProbeTimeout);
        }

        var probeToken = probeTimeoutCts?.Token ?? cancellationToken;

        try
        {
            if (!externalReconnect.NeedsReconnect
                && stack.DeploymentTarget == DeploymentTarget.External)
            {
                dockerContext = await ResolveDockerContextAsync(stack, probeToken);
            }

            var containerProbe = await GetContainersWithEngineStatusAsync(stack, dockerContext, probeToken);
            containers = containerProbe.Containers;
            dockerEngineAvailable = containerProbe.EngineReachable;
            dockerEngineUnavailableReason = containerProbe.EngineError;

            if (stack.DeploymentTarget == DeploymentTarget.External
                && dockerEngineAvailable == false
                && !externalReconnect.NeedsReconnect)
            {
                var (sshAvailable, sshMessage) = await _remoteEngine.ProbeRemoteDockerAsync(stack, probeToken);
                if (sshAvailable)
                {
                    dockerEngineAvailable = true;
                    dockerEngineUnavailableReason = null;
                }
                else if (!string.IsNullOrWhiteSpace(sshMessage))
                {
                    dockerEngineUnavailableReason = sshMessage;
                }
            }

            services = BuildServiceList(containers);
            if (stack.DeploymentTarget != DeploymentTarget.External)
            {
                await EnrichArmoryHealthAsync(stack.Id, services, containers, probeToken);
            }

            containers = ApplyServiceHealthToContainers(containers, services);
            runtimeStatus = DetermineRuntimeStatus(stack.Status, containers);

            if (stack.DeploymentTarget == DeploymentTarget.External)
            {
                CacheExternalRuntimeProbe(
                    stack.Id,
                    containers,
                    services,
                    runtimeStatus,
                    dockerEngineAvailable,
                    dockerEngineUnavailableReason);
            }
        }
        catch (OperationCanceledException) when (probeTimeoutCts?.IsCancellationRequested == true)
        {
            _logger.LogWarning("Timed out probing the remote Docker engine for stack {StackId}.", stack.Id);
            if (TryRestoreExternalRuntimeProbeCache(
                    stack.Id,
                    out containers,
                    out services,
                    out runtimeStatus,
                    out dockerEngineAvailable,
                    out _))
            {
                dockerEngineUnavailableReason =
                    "Timed out refreshing live status from the VPC; showing the last successful probe.";
            }
            else
            {
                containers = [];
                services = BuildServiceList(containers);
                runtimeStatus = stack.Status;
                dockerEngineAvailable = null;
                dockerEngineUnavailableReason =
                    "Timed out connecting to the remote Docker engine. The VPC may be under heavy load or unreachable. "
                    + "If a start/stop job is running, wait for it to finish and refresh.";
            }
        }

        return new RuntimeProbeResult(
            containers,
            services,
            runtimeStatus,
            dockerEngineAvailable,
            dockerEngineUnavailableReason);
    }

    private static async Task<T> WithExternalProbeLockAsync<T>(
        string stackId,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var gate = ExternalProbeLocks.GetOrAdd(stackId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
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
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return [];
        }

        return await GetContainersAsync(stack, cancellationToken);
    }

    private async Task<List<ContainerStatusDto>> GetContainersAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        var probe = await ListStackContainersAsync(stack, cancellationToken);
        return probe.Containers;
    }

    private async Task<(List<ContainerStatusDto> Containers, bool EngineReachable, string? EngineError)>
        ListStackContainersAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        try
        {
            var dockerContext = stack.DeploymentTarget == DeploymentTarget.External
                ? await ResolveDockerContextAsync(stack, cancellationToken)
                : null;

            var projectName = DockerComposeOverrideGenerator.GetComposeProjectName(stack.Id);
            var byProject = await _dockerService.ListContainersWithEngineStatusAsync(
                projectName,
                dockerContext,
                cancellationToken: cancellationToken);
            if (!byProject.EngineReachable)
            {
                return ([], false, byProject.EngineError);
            }

            var merged = byProject.Containers.ToDictionary(
                container => container.ContainerId,
                container => container,
                StringComparer.OrdinalIgnoreCase);

            var prefix = DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName);
            var byName = await _dockerService.ListContainersWithEngineStatusAsync(
                dockerContext: dockerContext,
                nameContains: prefix,
                cancellationToken: cancellationToken);
            if (byName.EngineReachable)
            {
                foreach (var container in byName.Containers)
                {
                    merged[container.ContainerId] = container;
                }
            }

            return (merged.Values.ToList(), true, null);
        }
        catch (Exception ex)
        {
            return ([], false, ex.Message);
        }
    }

    private async Task<(List<ContainerStatusDto> Containers, bool EngineReachable, string? EngineError)>
        GetContainersWithEngineStatusAsync(
            ManagedStackEntity stack,
            string? dockerContext,
            CancellationToken cancellationToken) =>
        await ListStackContainersAsync(stack, cancellationToken);

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

                await EnsureRuntimeConfigurationAsync(
                    armoryStack,
                    armoryRepo,
                    armoryToken,
                    includeArmory: true,
                    includeClient: armoryStack.ClientEnabled);
                await RunDockerComposeAsync(stackId, "restart frontend-armory", armoryRepo, armoryToken);
                return true;
            }

            // Start or Recreate: (re)build image, ensure DB, bring the armory up. Recreate forces a
            // fresh container so config/env changes are actually applied. Runs detached from the
            // request token (see StartArmoryInternalAsync).
            return await StartArmoryInternalAsync(stackId, action == StackServiceAction.Recreate);
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
                if (IsGameServerService(service))
                {
                    // Auth refuses to start when acore_auth.realmlist is empty or holds an unresolvable
                    // address. Full stack start seeds this in BringStackUpAsync; per-container starts must too.
                    await UpdateRealmlistAddressAsync(stack, throwOnFailure: true, cancellationToken);
                }

                if (action == StackServiceAction.Recreate)
                {
                    await PrepareFixedNameServiceRecreateAsync(stackId, stack, service, repoPath, cancellationToken);
                }

                var recreate = action == StackServiceAction.Recreate ? " --force-recreate" : string.Empty;
                await RunDockerComposeAsync(stackId, $"up -d{recreate} {service}", repoPath, cancellationToken);
                break;

            case StackServiceAction.Stop:
                if (await IsComposeServiceRestartingAsync(stackId, stack, service, cancellationToken))
                {
                    await ForceStopComposeServiceAsync(stackId, service, repoPath, cancellationToken);
                }
                else
                {
                    await RunDockerComposeAsync(stackId, $"stop -t 30 {service}", repoPath, cancellationToken);
                }

                break;

            case StackServiceAction.Restart:
                if (IsGameServerService(service))
                {
                    await UpdateRealmlistAddressAsync(stack, throwOnFailure: true, cancellationToken);
                }

                await RunDockerComposeAsync(stackId, $"restart {service}", repoPath, cancellationToken);
                break;
        }

        return true;
    }

    private static bool IsGameServerService(string service) =>
        string.Equals(service, "ac-authserver", StringComparison.OrdinalIgnoreCase)
        || string.Equals(service, "ac-worldserver", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stops a compose service and removes its container so <c>restart: unless-stopped</c> cannot respawn it.
    /// </summary>
    private async Task ForceStopComposeServiceAsync(
        string stackId,
        string service,
        string repoPath,
        CancellationToken cancellationToken)
    {
        await RunDockerComposeAsync(stackId, $"stop -t 5 {service}", repoPath, cancellationToken, throwOnError: false);
        await RunDockerComposeAsync(stackId, $"rm -sf {service}", repoPath, cancellationToken, throwOnError: false);
    }

    private async Task<bool> IsComposeServiceRestartingAsync(
        string stackId,
        ManagedStackEntity stack,
        string composeService,
        CancellationToken cancellationToken)
    {
        var containerName = DockerComposeOverrideGenerator.GetContainerNameForService(
            stack.Id,
            stack.StackName,
            composeService);
        if (containerName is null)
        {
            return false;
        }

        var probe = await ListStackContainersAsync(stack, cancellationToken);
        if (!probe.EngineReachable)
        {
            return false;
        }

        var container = probe.Containers.FirstOrDefault(item =>
            string.Equals(item.Name, containerName, StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains(containerName, StringComparison.OrdinalIgnoreCase));
        return container is not null
               && container.Status.Contains("restarting", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] StackContainerServices =
    [
        "ac-database",
        "ac-authserver",
        "ac-worldserver",
        "frontend-armory",
        "armory-assets",
        "client",
        "ac-db-import",
        "ac-client-data-init",
        "ac-tools",
        "ac-dev-server",
    ];

    private async Task ForceRemoveAllStackContainersByNameAsync(
        string stackId,
        ManagedStackEntity stack,
        CancellationToken cancellationToken)
    {
        var contextArg = await GetDockerContextArgAsync(stackId, cancellationToken);
        foreach (var service in StackContainerServices)
        {
            var containerName = DockerComposeOverrideGenerator.GetContainerNameForService(
                stack.Id,
                stack.StackName,
                service);
            if (containerName is null)
            {
                continue;
            }

            var (exitCode, _, stderr) = await RunDockerCliAsync($"{contextArg}rm -f {containerName}", cancellationToken);
            if (exitCode != 0
                && !stderr.Contains("No such container", StringComparison.OrdinalIgnoreCase)
                && !stderr.Contains("is not running", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "docker rm -f {ContainerName} on stack {StackId} exited {ExitCode}: {Stderr}",
                    containerName,
                    stackId,
                    exitCode,
                    stderr.Trim());
            }
        }
    }

    private async Task RemoveRemainingStackContainersAsync(
        string stackId,
        ManagedStackEntity stack,
        CancellationToken cancellationToken)
    {
        var probe = await ListStackContainersAsync(stack, cancellationToken);
        if (!probe.EngineReachable)
        {
            _logger.LogWarning(
                "Could not list containers while cleaning up stack {StackId}: {Error}",
                stackId,
                probe.EngineError);
            await ForceRemoveAllStackContainersByNameAsync(stackId, stack, cancellationToken);
            return;
        }

        if (probe.Containers.Count == 0)
        {
            return;
        }

        var prefix = DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName);
        var contextArg = await GetDockerContextArgAsync(stackId, cancellationToken);
        foreach (var container in probe.Containers.Where(c =>
                     IsActiveContainer(c) || c.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var (exitCode, _, stderr) = await RunDockerCliAsync($"{contextArg}rm -f {container.Name}", cancellationToken);
            if (exitCode != 0
                && !stderr.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "docker rm -f {ContainerName} on stack {StackId} exited {ExitCode}: {Stderr}",
                    container.Name,
                    stackId,
                    exitCode,
                    stderr.Trim());
            }
        }
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

    private static void CacheExternalRuntimeProbe(
        string stackId,
        List<ContainerStatusDto> containers,
        List<StackServiceDto> services,
        StackStatus runtimeStatus,
        bool? engineReachable,
        string? engineError)
    {
        ExternalRuntimeProbeCaches[stackId] = new ExternalRuntimeProbeCache(
            containers,
            services,
            runtimeStatus,
            engineReachable,
            engineError,
            DateTime.UtcNow);
    }

    private static bool TryRestoreExternalRuntimeProbeCache(
        string stackId,
        out List<ContainerStatusDto> containers,
        out List<StackServiceDto> services,
        out StackStatus runtimeStatus,
        out bool? engineReachable,
        out string? engineError)
    {
        if (ExternalRuntimeProbeCaches.TryGetValue(stackId, out var cached)
            && DateTime.UtcNow - cached.CachedAt <= ExternalRuntimeProbeCacheTtl)
        {
            containers = cached.Containers;
            services = cached.Services;
            runtimeStatus = cached.RuntimeStatus;
            engineReachable = cached.EngineReachable;
            engineError = cached.EngineError;
            return true;
        }

        containers = [];
        services = [];
        runtimeStatus = StackStatus.Stopped;
        engineReachable = null;
        engineError = null;
        return false;
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

        if (stack.ArmoryPort <= 0 || stack.ClientPort <= 0)
        {
            throw new InvalidOperationException(
                $"Stack '{stack.StackName}' is missing required published ports (armory={stack.ArmoryPort}, client={stack.ClientPort}).");
        }

        // Generate and persist a random armory session secret on first use (independent of the DB
        // password) so it can't be recomputed by anyone who only learns the DB credentials.
        if (renderArmory && string.IsNullOrEmpty(stack.ArmorySessionSecret))
        {
            stack.ArmorySessionSecret = GenerateArmorySessionSecret();
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (renderArmory)
        {
            await _armoryDatabase.EnsurePasswordAsync(stack.Id, cancellationToken);
            stack.ArmoryDatabasePasswordProtected = await _dbContext.ManagedStacks
                .AsNoTracking()
                .Where(item => item.Id == stack.Id)
                .Select(item => item.ArmoryDatabasePasswordProtected)
                .SingleAsync(cancellationToken);
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
        var publishBindIp = ResolvePublishBindIp(stack);
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
        await SeedStackVolumesAsync(stack, repoPath, renderArmory, renderClient, cancellationToken);
    }

    /// <summary>
    /// Creates and populates a stack's named volumes (modules, lua, etc, logs, client base/overlay/cache,
    /// armory assets) from the manager's local build directory. Runs against the stack's engine (the
    /// local daemon, or the remote engine for external stacks). Best-effort per volume so a transient
    /// hiccup on one does not abort the whole start.
    /// </summary>
    private async Task SeedStackVolumesAsync(
        ManagedStackEntity stack,
        string repoPath,
        bool seedArmoryAssets,
        bool seedClientVolumes,
        CancellationToken cancellationToken)
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
        if (seedArmoryAssets)
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
        if (seedClientVolumes)
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
    /// Serializes the incoming advanced config's per-service env map for persistence.
    /// </summary>
    private static string BuildEnvJson(AdvancedConfigDto advanced)
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

        return JsonSerializer.Serialize(perService, JsonOptions);
    }

    /// <summary>Reads the persisted per-service env map for the config DTO.</summary>
    private Dictionary<string, Dictionary<string, string>> BuildServiceEnvDto(ManagedStackEntity stack)
    {
        return Deserialize<Dictionary<string, Dictionary<string, string>>>(stack.ServiceEnvVarsJson)
            ?? new Dictionary<string, Dictionary<string, string>>();
    }

    private Dictionary<string, IReadOnlyDictionary<string, string>> BuildServiceEnvironment(ManagedStackEntity stack)
    {
        var perService = Deserialize<Dictionary<string, Dictionary<string, string>>>(stack.ServiceEnvVarsJson)
            ?? new Dictionary<string, Dictionary<string, string>>();

        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (serviceId, bucket) in perService)
        {
            result[serviceId] = bucket ?? new Dictionary<string, string>();
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
            // The container verifies player logins against this stack's auth DB on the compose network.
            LoginEnabled = true,
            RequireLogin = true,
            DbHost = DockerComposeOverrideGenerator.InternalDatabaseHost,
            DbPort = DockerComposeOverrideGenerator.InternalDatabasePort,
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
        var armoryDbPassword = string.IsNullOrWhiteSpace(stack.ArmoryDatabasePasswordProtected)
            ? throw new InvalidOperationException(
                $"Armory database credentials are not initialized for stack '{stack.Id}'. Regenerate runtime configuration.")
            : _secretProtector.Unprotect(stack.ArmoryDatabasePasswordProtected);
        return new ArmoryComposeOptions
        {
            ImageName = _armoryImageService.ImageNameFor(stack.Id),
            WebsiteName = string.IsNullOrWhiteSpace(realmName) ? "Armory" : $"{realmName}",
            RealmName = string.IsNullOrWhiteSpace(realmName) ? "AzerothCore" : realmName,
            RealmId = 1,
            // The armory reaches MySQL on the compose network (not the host-published port).
            DbHost = DockerComposeOverrideGenerator.InternalDatabaseHost,
            DbPort = DockerComposeOverrideGenerator.InternalDatabasePort,
            DbUser = ArmoryDatabaseProvisioningService.MysqlUsername,
            DbPassword = armoryDbPassword,
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
        var consecutiveHealthyPings = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var arguments =
                $"{contextArg}exec {containerName} mysqladmin ping -h127.0.0.1 -uroot " +
                $"-p{stack.DatabaseRootPassword} --silent";
            var (exitCode, _, _) = await RunDockerCliAsync(arguments, cancellationToken);
            if (exitCode == 0)
            {
                consecutiveHealthyPings++;
                if (consecutiveHealthyPings >= DatabaseReadyConsecutivePings)
                {
                    return;
                }
            }
            else
            {
                consecutiveHealthyPings = 0;
            }

            await Task.Delay(LifecyclePollInterval, cancellationToken);
        }

        throw new InvalidOperationException("MySQL did not accept connections before the startup timeout elapsed.");
    }

    private async Task WaitForStackToStopAsync(
        string stackId,
        ManagedStackEntity stack,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + LifecycleVerificationTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = await ListStackContainersAsync(stack, cancellationToken);
            if (!probe.EngineReachable)
            {
                await Task.Delay(LifecyclePollInterval, cancellationToken);
                continue;
            }

            if (probe.Containers.Count == 0 || probe.Containers.All(container => !IsActiveContainer(container)))
            {
                return;
            }

            await Task.Delay(LifecyclePollInterval, cancellationToken);
        }

        _logger.LogWarning(
            "Stack {StackId} still has active containers after the stop timeout; force-removing by name.",
            stackId);
        await ForceRemoveAllStackContainersByNameAsync(stackId, stack, cancellationToken);
        await RemoveRemainingStackContainersAsync(stackId, stack, cancellationToken);
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

    private static bool IsRunning(ContainerStatusDto container) =>
        container.Status.Contains("running", StringComparison.OrdinalIgnoreCase)
        || container.Status.Contains("up", StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveContainer(ContainerStatusDto container)
    {
        var status = container.Status.ToLowerInvariant();
        return status.Contains("running")
               || status.Contains("up")
               || status.Contains("restarting");
    }

    private static void EnsureStackLifecycleAllowed(ManagedStackEntity stack, string operation)
    {
        if (stack.Status == StackStatus.Building)
        {
            throw new InvalidOperationException($"Cannot {operation} stack '{stack.StackName}' while it is building.");
        }

        if (stack.Status == StackStatus.SetupIncomplete)
        {
            throw new InvalidOperationException(
                $"Cannot {operation} '{stack.StackName}' until you finish Create stack.");
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

    private static void ApplyCloudBinding(
        ManagedStackEntity stack,
        DeploymentConfigDto deployment,
        bool replaceEmpty)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External)
        {
            stack.CloudConnectionId = string.Empty;
            stack.CloudInstanceId = string.Empty;
            stack.CloudRegion = string.Empty;
            stack.CloudProvider = string.Empty;
            stack.CloudInstanceType = string.Empty;
            return;
        }

        SetIfProvided(value => stack.CloudConnectionId = value, deployment.CloudConnectionId, replaceEmpty);
        SetIfProvided(value => stack.CloudInstanceId = value, deployment.CloudInstanceId, replaceEmpty);
        SetIfProvided(value => stack.CloudRegion = value, deployment.CloudRegion, replaceEmpty);
        SetIfProvided(value => stack.CloudProvider = value, deployment.CloudProvider, replaceEmpty);
        SetIfProvided(value => stack.CloudInstanceType = value, deployment.CloudInstanceType, replaceEmpty);
    }

    private static void SetIfProvided(Action<string> assign, string? value, bool replaceEmpty)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (!replaceEmpty && string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        assign(trimmed);
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
        return await MapAsync(entity, probeRuntime: true, cancellationToken);
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
            ServiceEnvVarsJson = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
            {
                [ServiceEnvTemplateService.Worldserver] = discovered.DiscoveredEnvVars ?? new Dictionary<string, string>(),
            }),
            
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

        // Self-heal when the platform flag is stale in either direction.
        if (stack.IsAdminAccountInitialized)
        {
            try
            {
                if (await SoapAdminAccountExistsAsync(stack, cancellationToken))
                {
                    _logger.LogInformation("Admin account for stack {StackId} already initialized", stackId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not verify existing SOAP admin for stack {StackId} before init.",
                    stackId);
            }

            _logger.LogWarning(
                "Admin account flag set for stack {StackId} but no matching auth account exists; recreating.",
                stackId);
            stack.IsAdminAccountInitialized = false;
            stack.AdminAccountInitializedAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            try
            {
                if (await SoapAdminAccountExistsAsync(stack, cancellationToken))
                {
                    stack.IsAdminAccountInitialized = true;
                    stack.AdminAccountInitializedAt ??= DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation(
                        "Detected existing SOAP admin account for stack {StackId}; marking initialized.",
                        stackId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not verify existing SOAP admin for stack {StackId} before init.",
                    stackId);
            }
        }

        // Verify stack is running
        var composeProjectName = DockerComposeOverrideGenerator.GetComposeProjectName(stackId);
        var dockerContext = await ResolveDockerContextAsync(stack, cancellationToken);
        var stackContainers = await _dockerService.ListContainersAsync(
            composeProjectName,
            dockerContext,
            cancellationToken: cancellationToken);

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

    /// <summary>
    /// Returns the effective SOAP-admin flag for the UI. Promotes the platform flag when the auth account
    /// row already exists, or clears a stale flag when the row is missing (e.g. after a DB wipe).
    /// </summary>
    private async Task<(bool Initialized, DateTime? InitializedAt)> ReconcileSoapAdminFlagAsync(
        ManagedStackEntity stack,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await SoapAdminAccountExistsAsync(stack, cancellationToken))
            {
                if (!stack.IsAdminAccountInitialized)
                {
                    var tracked = await _dbContext.ManagedStacks
                        .SingleOrDefaultAsync(item => item.Id == stack.Id, cancellationToken);
                    if (tracked is not null)
                    {
                        tracked.IsAdminAccountInitialized = true;
                        tracked.AdminAccountInitializedAt ??= DateTime.UtcNow;
                        await _dbContext.SaveChangesAsync(cancellationToken);
                        _logger.LogInformation(
                            "Detected existing SOAP admin account for stack {StackId}; marking initialized.",
                            stack.Id);
                        return (true, tracked.AdminAccountInitializedAt);
                    }
                }

                return (true, stack.AdminAccountInitializedAt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Could not verify SOAP admin account for stack {StackId}; keeping platform flag.",
                stack.Id);
            return (stack.IsAdminAccountInitialized, stack.AdminAccountInitializedAt);
        }

        if (!stack.IsAdminAccountInitialized)
        {
            return (false, null);
        }

        var staleTracked = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(item => item.Id == stack.Id, cancellationToken);
        if (staleTracked is not null && staleTracked.IsAdminAccountInitialized)
        {
            staleTracked.IsAdminAccountInitialized = false;
            staleTracked.AdminAccountInitializedAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "Cleared stale SOAP admin flag for stack {StackId} — auth account row is missing.",
                stack.Id);
        }

        return (false, null);
    }

    private async Task<bool> SoapAdminAccountExistsAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(stack.Id, "auth", cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM account a
            INNER JOIN account_access aa ON aa.id = a.id
            WHERE UPPER(a.username) = UPPER(@username) AND aa.gmlevel >= 3
            """;
        var usernameParam = command.CreateParameter();
        usernameParam.ParameterName = "@username";
        usernameParam.Value = stack.SoapUsername;
        command.Parameters.Add(usernameParam);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && Convert.ToInt64(result) > 0;
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

        var perService = Deserialize<Dictionary<string, Dictionary<string, string>>>(stack.ServiceEnvVarsJson)
            ?? new Dictionary<string, Dictionary<string, string>>();
        if (!perService.TryGetValue(ServiceEnvTemplateService.Worldserver, out var existing) || existing is null)
        {
            existing = new Dictionary<string, string>();
            perService[ServiceEnvTemplateService.Worldserver] = existing;
        }

        foreach (var (key, value) in envVars)
        {
            existing[key] = value;
        }

        stack.ServiceEnvVarsJson = JsonSerializer.Serialize(perService, JsonOptions);
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

    public async Task<RemoteSetupResultDto?> ProvisionVpcDockerAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null || stack.DeploymentTarget != DeploymentTarget.External)
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
        var result = await _remoteEngine.ProvisionRemoteHostAsync(
            stack.ExternalHost,
            stack.ExternalSshPort,
            stack.ExternalSshUser,
            privateKey,
            new RemoteSetupOptionsDto
            {
                RemoteOs = RemoteHostOs.Linux,
                EnableHostFirewall = false,
                EnableUnattendedUpgrades = false,
                AuthServerPort = stack.AuthServerPort,
                WorldServerPort = stack.WorldServerPort,
                ArmoryPort = stack.ArmoryPort,
                ClientPort = stack.ClientPort,
                SshPort = stack.ExternalSshPort
            },
            timeoutCts.Token);

        if (result.Success)
        {
            await _remoteEngine.EnsureContextAsync(stack, cancellationToken);
        }

        return result;
    }

    public async Task<RemoteSetupResultDto?> FinalizeSshHardeningAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null || stack.DeploymentTarget != DeploymentTarget.External)
        {
            return null;
        }

        if (stack.Status == StackStatus.SetupIncomplete)
        {
            throw new InvalidOperationException("Finish stack setup before SSH hardening.");
        }

        if (string.IsNullOrWhiteSpace(stack.ExternalSshPrivateKey))
        {
            throw new InvalidOperationException("External stack is missing SSH credentials.");
        }

        var privateKey = _secretProtector.Unprotect(stack.ExternalSshPrivateKey);
        var enableAwsInstanceConnect = string.Equals(stack.CloudProvider, "Aws", StringComparison.OrdinalIgnoreCase);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
        var result = await _remoteEngine.FinalizeSshHardeningAsync(
            stack.ExternalHost,
            stack.ExternalSshPort,
            stack.ExternalSshUser,
            privateKey,
            enableAwsInstanceConnect,
            timeoutCts.Token);

        if (result.Success)
        {
            stack.SshHardeningCompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return result;
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

    public async Task<VpcFirewallStatusDto?> GetVpcFirewallStatusAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null || stack.DeploymentTarget != DeploymentTarget.External)
        {
            return null;
        }

        var profile = VpcSecurityCatalog.BuildProfile(
            stack.ExternalHost,
            stack.AuthServerPort,
            stack.WorldServerPort,
            stack.ArmoryPort,
            stack.ClientPort,
            stack.DatabasePort,
            stack.SoapPort,
            stack.ExternalSshPort);

        var status = await _remoteEngine.ProbeHostFirewallAsync(stack, profile, cancellationToken);
        await AppendDockerBindChecksAsync(stack, status, cancellationToken);

        status.OverallHealthy = status.Checks.Count == 0
            || status.Checks.All(c =>
                c.Status is "ok" or "unknown" or "not-applicable");
        status.Message = status.OverallHealthy
            ? "Host firewall and Docker bind checks passed. Cloud security group rules must still be verified manually."
            : "One or more security checks failed — review the items below.";

        return status;
    }

    public async Task<VpcSshLogsDto?> GetVpcSshLogsAsync(
        string stackId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null || stack.DeploymentTarget != DeploymentTarget.External)
        {
            return null;
        }

        return await _remoteEngine.FetchSshAuthLogsAsync(stack, limit, cancellationToken);
    }

    private async Task AppendDockerBindChecksAsync(
        ManagedStackEntity stack,
        VpcFirewallStatusDto status,
        CancellationToken cancellationToken)
    {
        var prefix = DockerComposeOverrideGenerator.GetContainerPrefix(stack.Id, stack.StackName);
        var bindChecks = new (string Container, int InternalPort, string Name, string RoleId, int HostPort)[]
        {
            ($"{prefix}-database", DockerComposeOverrideGenerator.InternalDatabasePort, "MySQL Docker bind", VpcSecurityCatalog.RoleManagement, stack.DatabasePort),
            ($"{prefix}-worldserver", 7878, "SOAP Docker bind", VpcSecurityCatalog.RoleManagement, stack.SoapPort),
        };

        foreach (var (container, internalPort, name, roleId, hostPort) in bindChecks)
        {
            var check = new VpcSecurityCheckDto
            {
                Category = "docker-bind",
                Name = name,
                RoleId = roleId,
                Port = hostPort
            };

            try
            {
                var endpoint = await _remoteEngine.TryResolveRemotePublishedEndpointAsync(
                    stack,
                    container,
                    internalPort,
                    cancellationToken);
                if (endpoint is null)
                {
                    check.Status = "warning";
                    check.Message = $"{container} is not publishing TCP {internalPort} — stack may be stopped.";
                }
                else if (IsLoopbackBind(endpoint.Value.Host))
                {
                    check.Status = "ok";
                    check.Message = $"Published on {endpoint.Value.Host}:{endpoint.Value.Port} (manager/VPC-only).";
                }
                else if (string.Equals(endpoint.Value.Host, "0.0.0.0", StringComparison.Ordinal))
                {
                    check.Status = "error";
                    check.Message = $"Published on 0.0.0.0:{endpoint.Value.Port} — should bind to 127.0.0.1 on external stacks.";
                }
                else
                {
                    check.Status = "warning";
                    check.Message = $"Published on {endpoint.Value.Host}:{endpoint.Value.Port} — verify this is not publicly reachable.";
                }
            }
            catch (Exception ex)
            {
                check.Status = "unknown";
                check.Message = ex.Message;
            }

            status.Checks.Add(check);
        }
    }

    private static bool IsLoopbackBind(string host)
        => string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
           || string.Equals(host, "::1", StringComparison.Ordinal)
           || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    private string ResolveExternalDataPlaneBind(ManagedStackEntity stack)
    {
        // The manager reaches MySQL/SOAP on external stacks via SSH -L …:127.0.0.1:{port} on the remote
        // host. Those services must publish on loopback there; binding to a public/VPC IP breaks the tunnel
        // ("Reading from the stream has failed") even when SSH itself still works.
        var configured = TryParseBindAddress(_dockerOptions.ExternalDataPlaneBindAddress);
        if (configured is not null)
        {
            return configured;
        }

        if (!string.IsNullOrWhiteSpace(_dockerOptions.ExternalDataPlaneBindAddress))
        {
            _logger.LogWarning(
                "Docker:ExternalDataPlaneBindAddress '{Bind}' is not a valid IP; using 127.0.0.1 on external stacks.",
                _dockerOptions.ExternalDataPlaneBindAddress.Trim());
        }

        return "127.0.0.1";
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

    /// <summary>
    /// Resolves the IP Docker should bind for armory/client ports. External stacks ignore public/elastic
    /// IPs stored in <see cref="ManagedStackEntity.PublishBindAddress"/> (realmlist host ≠ bind address).
    /// </summary>
    private string? ResolvePublishBindIp(ManagedStackEntity stack)
    {
        var configured = TryParseBindAddress(stack.PublishBindAddress);
        if (configured is null)
        {
            return null;
        }

        if (string.Equals(configured, "0.0.0.0", StringComparison.Ordinal))
        {
            return configured;
        }

        if (!System.Net.IPAddress.TryParse(configured, out var parsed)
            || !IsDockerPublishBindAddress(parsed))
        {
            _logger.LogWarning(
                "Ignoring non-bindable PublishBindAddress '{Bind}' for stack {StackId}; using policy default.",
                configured,
                stack.Id);
            return null;
        }

        if (stack.DeploymentTarget == DeploymentTarget.External
            && !RealmlistHostResolver.IsPrivateOrNonRoutableIpv4Literal(configured))
        {
            _logger.LogWarning(
                "PublishBindAddress '{Bind}' is a public IP and cannot be bound on the remote VPC host for stack {StackId}; using all interfaces.",
                configured,
                stack.Id);
            return null;
        }

        return configured;
    }

    private static bool IsDockerPublishBindAddress(System.Net.IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    private async Task<string> ResolveAndMaybeVaultDeploymentKeyAsync(
        DeploymentConfigDto deployment,
        CancellationToken cancellationToken)
    {
        var pem = await DeploymentSshKeyResolver.ResolvePrivateKeyAsync(
            deployment,
            _cloudSshKeyService,
            "stack",
            cancellationToken);

        if (deployment.SaveSshKeyToVault
            && !string.IsNullOrWhiteSpace(deployment.ExternalSshPrivateKey)
            && string.IsNullOrWhiteSpace(deployment.SavedSshKeyId))
        {
            try
            {
                await _cloudSshKeyService.CreateAsync(
                    new CreateCloudSshKeyRequestDto
                    {
                        Label = deployment.SaveSshKeyLabel,
                        PrivateKey = pem,
                        DefaultSshUser = deployment.ExternalSshUser,
                    },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save SSH key to the vault during stack creation.");
            }
        }

        return pem;
    }

    private async Task<string> ResolveReconnectPrivateKeyAsync(
        DeploymentConfigDto deployment,
        ManagedStackEntity stack,
        CancellationToken cancellationToken)
    {
        if (DeploymentSshKeyResolver.HasResolvableKey(deployment))
        {
            return await DeploymentSshKeyResolver.ResolvePrivateKeyAsync(
                deployment,
                _cloudSshKeyService,
                "stack",
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(stack.ExternalSshPrivateKey))
        {
            throw new InvalidOperationException("SSH private key is required to reconnect.");
        }

        return _secretProtector.Unprotect(stack.ExternalSshPrivateKey);
    }
}
