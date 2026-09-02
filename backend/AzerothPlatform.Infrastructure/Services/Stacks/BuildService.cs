using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Orchestrates AzerothCore builds: clone, configure, build Docker images, stream progress.
/// </summary>
public sealed class BuildService : IBuildService
{
    private const string BuildStatusFileName = "build-status.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly ConcurrentDictionary<string, BuildStatusDto> BuildStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> BuildCancellations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> ModuleSyncLocks = new(StringComparer.OrdinalIgnoreCase);
    
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _buildsPath;
    private readonly DockerOptions _dockerOptions;
    private readonly MigrationOptions _migrationOptions;
    private readonly IBuildEventPublisher _eventPublisher;
    private readonly IStackImageShippingService _stackImageShipping;
    private readonly IServerTypeCatalog _serverTypeCatalog;
    private readonly ILogger<BuildService> _logger;

    public BuildService(
        IServiceScopeFactory scopeFactory,
        IOptions<DockerOptions> dockerOptions,
        IOptions<MigrationOptions> migrationOptions,
        IBuildEventPublisher eventPublisher,
        IStackImageShippingService stackImageShipping,
        IServerTypeCatalog serverTypeCatalog,
        ILogger<BuildService> logger)
    {
        _scopeFactory = scopeFactory;
        _dockerOptions = dockerOptions.Value;
        _migrationOptions = migrationOptions.Value;
        _eventPublisher = eventPublisher;
        _stackImageShipping = stackImageShipping;
        _serverTypeCatalog = serverTypeCatalog;
        _logger = logger;
        
        // Resolve relative paths from the current directory (project root when using dotnet run/watch)
        var configuredPath = _dockerOptions.BuildsPath;
        _buildsPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath);
        
        // Ensure the builds directory exists
        Directory.CreateDirectory(_buildsPath);
        _logger.LogInformation("Builds path resolved to: {BuildsPath}", _buildsPath);
    }

    public async Task<BuildStatusDto> StartAsync(
        string stackId,
        StackConfigurationDto? configuration = null,
        CancellationToken cancellationToken = default,
        bool skipModuleCheck = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        
        var stack = await dbContext.ManagedStacks.SingleAsync(item => item.Id == stackId, cancellationToken);

        if (BuildStates.TryGetValue(stackId, out var existingBuild) && !IsTerminalPhase(existingBuild.CurrentPhase))
        {
            if (existingBuild.CurrentPhase is BuildPhase.CheckingModules)
            {
                throw new InvalidOperationException("Cannot build Docker images while a module check is in progress.");
            }

            _logger.LogWarning("Cancelling existing build for stack {StackId} to start rebuild", stackId);
            if (BuildCancellations.TryGetValue(stackId, out var existingCts))
            {
                existingCts.Cancel();
            }
        }

        // If no configuration provided, use existing stack configuration (for rebuilds)
        var buildConfig = configuration ?? new StackConfigurationDto
        {
            StackName = stack.StackName,
            ServerType = stack.ServerType,
            ModuleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson) ?? [],
            ModuleBranches = ModuleBranchResolver.Parse(stack.ModuleBranchesJson),
            Database = new DatabaseConfigDto
            {
                RootPassword = stack.DatabaseRootPassword,
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
                ServiceEnvVars = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stack.ServiceEnvVarsJson)
                    ?? new Dictionary<string, Dictionary<string, string>>()
            }
        };
        
        if (buildConfig is null)
        {
            throw new InvalidOperationException("Configuration is required to start a build (no existing configuration found)");
        }

        if (buildConfig.ModuleBranches.Count == 0)
        {
            buildConfig.ModuleBranches = ModuleBranchResolver.Parse(stack.ModuleBranchesJson);
        }

        if (buildConfig.ModuleIds.Count == 0)
        {
            var moduleIdsFromDb = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson) ?? [];
            if (moduleIdsFromDb.Count > 0)
            {
                _logger.LogWarning(
                    "Build request for stack {StackId} had no module IDs; using {Count} module(s) from the saved stack configuration",
                    stackId,
                    moduleIdsFromDb.Count);
                buildConfig.ModuleIds = moduleIdsFromDb;
            }
        }
        
        // Debug logging for rebuilds
        if (configuration is null)
        {
            _logger.LogInformation("Rebuild: Using existing config - DB Password length: {PasswordLength}, Stack: {StackName}",
                buildConfig.Database.RootPassword?.Length ?? 0, buildConfig.StackName);
        }

        _logger.LogInformation(
            "Build for stack {StackId} will compile {ModuleCount} module(s): {ModuleIds}",
            stackId,
            buildConfig.ModuleIds.Count,
            string.Join(", ", buildConfig.ModuleIds));

        await EnsureModuleCheckGateAsync(stack, buildConfig, skipModuleCheck, cancellationToken);
        if (skipModuleCheck)
        {
            stack.ModuleCheckJson = JsonSerializer.Serialize(
                new ModuleCheckStatusDto
                {
                    Passed = false,
                    Skipped = true,
                    CompletedAt = DateTime.UtcNow,
                    Items = []
                },
                JsonOptions);
        }

        var buildStatus = new BuildStatusDto
        {
            BuildId = Guid.NewGuid().ToString("N"),
            CurrentPhase = BuildPhase.Cloning,
            ProgressPercent = 0,
            CurrentStep = "Initializing build...",
            RecentLogs = [$"Starting build for stack '{stack.StackName}'"],
            StartedAt = DateTime.UtcNow
        };

        BuildStates[stackId] = buildStatus;
        PersistBuildStatus(stackId, buildStatus);
        stack.Status = StackStatus.Building;
        await dbContext.SaveChangesAsync(cancellationToken);

        var buildCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        BuildCancellations[stackId] = buildCts;

        _ = Task.Run(async () => await ExecuteBuildAsync(stackId, stack.StackName, buildConfig, buildCts.Token), CancellationToken.None);

        return buildStatus;
    }

    public async Task<BuildStatusDto> CheckModulesAsync(string stackId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack '{stackId}' was not found.");

        if (BuildStates.TryGetValue(stackId, out var existingBuild) && !IsTerminalPhase(existingBuild.CurrentPhase))
        {
            if (existingBuild.CurrentPhase is BuildPhase.CreatingImages or BuildPhase.Building
                or BuildPhase.Cloning or BuildPhase.PreparingModules)
            {
                throw new InvalidOperationException("Cannot check modules while a Docker image build is in progress.");
            }

            _logger.LogWarning("Cancelling existing job for stack {StackId} to start a module check", stackId);
            if (BuildCancellations.TryGetValue(stackId, out var existingCts))
            {
                existingCts.Cancel();
            }
        }

        var buildConfig = new StackConfigurationDto
        {
            StackName = stack.StackName,
            ServerType = stack.ServerType,
            ModuleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson) ?? [],
            ModuleBranches = ModuleBranchResolver.Parse(stack.ModuleBranchesJson),
            Database = new DatabaseConfigDto
            {
                RootPassword = stack.DatabaseRootPassword,
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
                RealmName = stack.RealmName
            },
            CustomFork = string.IsNullOrWhiteSpace(stack.CoreRepositoryUrl)
                ? null
                : new CustomForkConfigDto { RepositoryUrl = stack.CoreRepositoryUrl, Branch = stack.CoreBranch }
        };

        stack.ModuleCheckFingerprint = string.Empty;
        var buildStatus = new BuildStatusDto
        {
            BuildId = Guid.NewGuid().ToString("N"),
            CurrentPhase = BuildPhase.CheckingModules,
            ProgressPercent = 0,
            CurrentStep = "Preparing module compile check...",
            RecentLogs = [$"Checking modules for stack '{stack.StackName}'"],
            StartedAt = DateTime.UtcNow
        };

        BuildStates[stackId] = buildStatus;
        PersistBuildStatus(stackId, buildStatus);
        stack.Status = StackStatus.Building;
        await dbContext.SaveChangesAsync(cancellationToken);

        var buildCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        BuildCancellations[stackId] = buildCts;
        _ = Task.Run(async () => await ExecuteModuleCheckAsync(stackId, buildConfig, buildCts.Token), CancellationToken.None);
        return buildStatus;
    }

    public async Task<SyncStackModulesResultDto> SyncModulesAsync(
        string stackId,
        string? moduleId = null,
        CancellationToken cancellationToken = default)
    {
        if (!ModuleSyncLocks.TryAdd(stackId, 0))
        {
            throw new InvalidOperationException("A module update is already running for this stack.");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            var stack = await dbContext.ManagedStacks
                .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
            if (stack is null)
            {
                throw new KeyNotFoundException($"Stack '{stackId}' was not found.");
            }

            if (stack.Status == StackStatus.Building
                || (BuildStates.TryGetValue(stackId, out var inProgress)
                    && !IsTerminalPhase(inProgress.CurrentPhase)))
            {
                throw new InvalidOperationException("Cannot update modules while a build is in progress.");
            }

            var selectedIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson) ?? [];
            var branches = ModuleBranchResolver.Parse(stack.ModuleBranchesJson);
            if (selectedIds.Count == 0)
            {
                return new SyncStackModulesResultDto();
            }

            IReadOnlyList<string> targetIds = selectedIds;
            if (!string.IsNullOrWhiteSpace(moduleId))
            {
                if (!selectedIds.Contains(moduleId, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Module '{moduleId}' is not selected on this stack.");
                }

                targetIds = [moduleId];
            }

            var moduleCatalog = scope.ServiceProvider.GetRequiredService<IModuleCatalogService>();
            var allModules = await moduleCatalog.ListAsync(stack.ServerType, cancellationToken);
            var modulesPath = Path.Combine(_buildsPath, stackId, "azerothcore-wotlk", "modules");
            Directory.CreateDirectory(modulesPath);

            var result = new SyncStackModulesResultDto();
            foreach (var id in targetIds)
            {
                var module = allModules.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                var resolved = module is null
                    ? null
                    : ModuleBranchResolver.WithBranch(
                        module,
                        ModuleBranchResolver.Resolve(module, branches, selectedIds, allModules));
                result.Items.Add(await SyncOneModuleAsync(id, resolved, modulesPath, cancellationToken));
            }

            foreach (var companion in ModuleCompileEnvironment.CompanionsFor(targetIds, allModules))
            {
                if (selectedIds.Contains(companion.Id, StringComparer.OrdinalIgnoreCase)
                    || result.Items.Any(item => item.ModuleId.Equals(companion.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                result.Items.Add(await SyncOneModuleAsync(
                    companion.Id,
                    ModuleCompileEnvironment.ToModuleDto(companion),
                    modulesPath,
                    cancellationToken));
            }

            RemoveUnselectedModuleDirectories(
                modulesPath,
                ModuleCompileEnvironment.ModuleDirectoriesToKeep(selectedIds, allModules));

            var hooks = scope.ServiceProvider.GetRequiredService<IModuleInstallHookRunner>();
            hooks.PrepareCheckouts(modulesPath);

            stack.ModuleCheckFingerprint = string.Empty;
            await dbContext.SaveChangesAsync(cancellationToken);
            return result;
        }
        finally
        {
            ModuleSyncLocks.TryRemove(stackId, out _);
        }
    }

    private async Task<SyncStackModuleItemDto> SyncOneModuleAsync(
        string moduleId,
        ModuleDto? module,
        string modulesPath,
        CancellationToken cancellationToken)
    {
        if (module is null)
        {
            return new SyncStackModuleItemDto
            {
                ModuleId = moduleId,
                Name = moduleId,
                Ok = false,
                Skipped = true,
                Message = "This module is not in the current catalog, so it cannot be pulled from GitHub."
            };
        }

        if (module.SourceType == ModuleSource.Package)
        {
            return new SyncStackModuleItemDto
            {
                ModuleId = module.Id,
                Name = module.Name,
                Ok = true,
                Skipped = true,
                Message = "Uploaded package — GitHub sync does not apply. Replace the package from the module catalog."
            };
        }

        var moduleDir = ModuleCompileEnvironment.ModuleDirectory(modulesPath, module);
        try
        {
            string? shaBefore = null;
            var cloned = false;
            if (Directory.Exists(moduleDir) && !IsGitCheckout(moduleDir))
            {
                Directory.Delete(moduleDir, recursive: true);
            }

            if (Directory.Exists(moduleDir)
                && !await GitOriginMatchesAsync(moduleDir, module.Repository, cancellationToken))
            {
                Directory.Delete(moduleDir, recursive: true);
            }

            if (Directory.Exists(moduleDir))
            {
                shaBefore = await TryGetCommitShaAsync(moduleDir, cancellationToken);
                try
                {
                    await ResetGitCheckoutToOriginAsync(moduleDir, module.Branch, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    Directory.Delete(moduleDir, recursive: true);
                    cloned = true;
                    await CloneGitModuleAsync(modulesPath, module, cancellationToken);
                }
            }
            else
            {
                cloned = true;
                await CloneGitModuleAsync(modulesPath, module, cancellationToken);
            }

            var sha = await TryGetCommitShaAsync(moduleDir, cancellationToken);
            var shortSha = string.IsNullOrEmpty(sha) ? null : sha[..Math.Min(7, sha.Length)];
            var unchanged = !cloned
                && !string.IsNullOrEmpty(shaBefore)
                && string.Equals(shaBefore, sha, StringComparison.OrdinalIgnoreCase);
            var message = cloned
                ? $"Cloned from GitHub{(shortSha is null ? "." : $" ({shortSha}).")}"
                : unchanged
                    ? $"Already up to date on {module.Branch}{(shortSha is null ? "." : $" ({shortSha}).")}"
                    : $"Updated to latest {module.Branch}{(shortSha is null ? "." : $" ({shortSha}).")}";

            var includeFix = ModuleCompileEnvironment.FixCaseMismatchedIncludes(moduleDir);
            if (!string.IsNullOrEmpty(includeFix))
            {
                message = $"{message} {includeFix}";
            }

            return new SyncStackModuleItemDto
            {
                ModuleId = module.Id,
                Name = module.Name,
                Ok = true,
                Cloned = cloned,
                CommitSha = sha,
                Message = message
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync module {ModuleId}", module.Id);
            return new SyncStackModuleItemDto
            {
                ModuleId = module.Id,
                Name = module.Name,
                Ok = false,
                Message = ex.Message
            };
        }
    }

    private async Task CloneGitModuleAsync(
        string modulesPath,
        ModuleDto module,
        CancellationToken cancellationToken,
        string? logStackId = null)
    {
        var safeBranch = ModuleCatalogService.ValidateGitRef(module.Branch);
        var safeRepo = ModuleCatalogService.ValidateGitRepository(module.Repository);
        var destFolder = ModuleCompileEnvironment.CheckoutFolder(module);
        var args = new[] { "clone", "--depth", "1", "--branch", safeBranch, "--", safeRepo, destFolder };
        if (logStackId is null)
        {
            await RunGitOrThrowAsync(args, modulesPath, cancellationToken, TimeSpan.FromMinutes(5));
            return;
        }

        await RunProcessArgsAsync(logStackId, "git", args, modulesPath, cancellationToken);
    }

    private async Task ResetGitCheckoutToOriginAsync(
        string moduleDir,
        string branch,
        CancellationToken cancellationToken,
        string? logStackId = null)
    {
        var safeBranch = ModuleCatalogService.ValidateGitRef(branch);
        if (logStackId is null)
        {
            await RunGitOrThrowAsync(["fetch", "--depth", "1", "origin", safeBranch], moduleDir, cancellationToken, TimeSpan.FromMinutes(3));
            await RunGitOrThrowAsync(["reset", "--hard", "FETCH_HEAD"], moduleDir, cancellationToken, TimeSpan.FromMinutes(1));
            await RunGitOrThrowAsync(["clean", "-ffd"], moduleDir, cancellationToken, TimeSpan.FromMinutes(1));
            return;
        }

        await RunProcessArgsAsync(logStackId, "git", ["fetch", "--depth", "1", "origin", safeBranch], moduleDir, cancellationToken);
        await RunProcessArgsAsync(logStackId, "git", ["reset", "--hard", "FETCH_HEAD"], moduleDir, cancellationToken);
        await RunProcessArgsAsync(logStackId, "git", ["clean", "-ffd"], moduleDir, cancellationToken);
    }

    private static bool IsTerminalPhase(BuildPhase phase) =>
        phase is BuildPhase.Completed or BuildPhase.Failed or BuildPhase.ModuleCheckPassed;

    private static bool HasCompletedWorldserverBuild(ManagedStackEntity stack) =>
        stack.LastBuiltAt.HasValue || !string.IsNullOrEmpty(stack.CoreCommitSha);

    private async Task EnsureModuleCheckGateAsync(
        ManagedStackEntity stack,
        StackConfigurationDto configuration,
        bool skipModuleCheck,
        CancellationToken cancellationToken)
    {
        if (skipModuleCheck
            || configuration.ModuleIds.Count == 0
            || HasCompletedWorldserverBuild(stack)
            || IsModuleCheckSkipped(stack))
        {
            return;
        }

        var current = await ComputeCheckoutFingerprintAsync(stack.Id, configuration, cancellationToken);
        if (string.IsNullOrWhiteSpace(stack.ModuleCheckFingerprint)
            || !string.Equals(stack.ModuleCheckFingerprint, current, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Selected modules have not been compile-checked against this core yet. Run Check modules first, or skip the check.");
        }
    }

    private static bool IsModuleCheckSkipped(ManagedStackEntity stack)
    {
        if (string.IsNullOrWhiteSpace(stack.ModuleCheckJson))
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ModuleCheckStatusDto>(stack.ModuleCheckJson, JsonOptions);
            return dto?.Skipped == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<string> ComputeCheckoutFingerprintAsync(
        string stackId,
        StackConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        var items = await CollectCheckoutModuleItemsAsync(stackId, configuration, cancellationToken);
        var repoPath = Path.Combine(_buildsPath, stackId, "azerothcore-wotlk");
        var coreSha = Directory.Exists(repoPath)
            ? await TryGetCommitShaAsync(repoPath, cancellationToken)
            : null;
        return ModuleCheckCompiler.ComputeFingerprint(coreSha, items);
    }

    private async Task<List<ModuleCheckItemDto>> CollectCheckoutModuleItemsAsync(
        string stackId,
        StackConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var moduleCatalog = scope.ServiceProvider.GetRequiredService<IModuleCatalogService>();
        var allModules = await moduleCatalog.ListAsync(configuration.ServerType, cancellationToken);
        var repoPath = Path.Combine(_buildsPath, stackId, "azerothcore-wotlk");
        var items = new List<ModuleCheckItemDto>();
        foreach (var moduleId in configuration.ModuleIds)
        {
            var module = allModules.FirstOrDefault(m => m.Id.Equals(moduleId, StringComparison.OrdinalIgnoreCase));
            var branch = module is null
                ? null
                : ModuleBranchResolver.Resolve(module, configuration.ModuleBranches, configuration.ModuleIds, allModules);
            var moduleDir = ModuleCompileEnvironment.ModuleDirectory(
                Path.Combine(repoPath, "modules"),
                module ?? new ModuleDto { Id = moduleId });
            var sha = Directory.Exists(moduleDir)
                ? await TryGetCommitShaAsync(moduleDir, cancellationToken)
                : null;
            items.Add(new ModuleCheckItemDto
            {
                ModuleId = moduleId,
                Name = module?.Name ?? moduleId,
                Branch = branch,
                CommitSha = sha,
                CheckoutFolder = ModuleCompileEnvironment.CheckoutFolder(module, moduleId)
            });
        }

        foreach (var companion in ModuleCompileEnvironment.CompanionsFor(configuration.ModuleIds, allModules))
        {
            if (items.Any(item => item.ModuleId.Equals(companion.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var companionDir = ModuleCompileEnvironment.ModuleDirectory(Path.Combine(repoPath, "modules"), companion.Id);
            items.Add(new ModuleCheckItemDto
            {
                ModuleId = companion.Id,
                Name = companion.Name,
                Branch = companion.Branch,
                CommitSha = Directory.Exists(companionDir)
                    ? await TryGetCommitShaAsync(companionDir, cancellationToken)
                    : null
            });
        }

        return items;
    }

    private async Task ExecuteModuleCheckAsync(
        string stackId,
        StackConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var buildPath = Path.Combine(_buildsPath, stackId);
            Directory.CreateDirectory(buildPath);
            Services.Patches.MigrationLayout.EnsureScaffold(buildPath, _migrationOptions.ClientSettingsTemplatePath);
            await RunProcessAsync(stackId, "git", "config --global --add safe.directory *", buildPath, cancellationToken);

            string repoUrl;
            string branch;
            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
                var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
                if (stack is not null && !string.IsNullOrEmpty(stack.CoreRepositoryUrl))
                {
                    repoUrl = stack.CoreRepositoryUrl;
                    branch = !string.IsNullOrEmpty(stack.CoreBranch) ? stack.CoreBranch : "master";
                }
                else
                {
                    (repoUrl, branch) = _serverTypeCatalog.GetCoreRepository(configuration.ServerType);
                    if (stack is not null)
                    {
                        stack.CoreRepositoryUrl = repoUrl;
                        stack.CoreBranch = branch;
                        await dbContext.SaveChangesAsync(cancellationToken);
                    }
                }
            }

            if (configuration.ModuleIds.Count == 0)
            {
                await AddLogAsync(stackId, "No modules selected; skipping the compile check.");
                await CompleteModuleCheckAsync(stackId, configuration, passed: true, [], cancellationToken);
                return;
            }

            await CloneRepositoryAsync(
                stackId, buildPath, repoUrl, branch, configuration, cancellationToken,
                phase: BuildPhase.CheckingModules, refreshIfPresent: true);
            await PrepareModulesAsync(
                stackId, buildPath, configuration, cancellationToken,
                phase: BuildPhase.CheckingModules, refreshIfPresent: true);

            var checkRepoPath = Path.Combine(buildPath, "azerothcore-wotlk");
            await InjectModuleBuildPackagesAsync(stackId, checkRepoPath, configuration, cancellationToken);

            await UpdateBuildStatusAsync(
                stackId, BuildPhase.CheckingModules, 45, "Building the module-check compiler image...", null);
            var compiler = new ModuleCheckCompiler(_dockerOptions, _logger, stackId);
            await compiler.EnsureImageAsync(message => AddLogAsync(stackId, message), cancellationToken);

            var repoPath = Path.Combine(buildPath, "azerothcore-wotlk");
            var (volumeArgs, workDir) = compiler.ResolveMount(repoPath);
            await UpdateBuildStatusAsync(
                stackId,
                BuildPhase.CheckingModules,
                50,
                "Configuring CMake (first run compiles core libraries and can take a while)...",
                null);
            await compiler.ConfigureAsync(volumeArgs, workDir, message => AddLogAsync(stackId, message), cancellationToken);

            using var catalogScope = _scopeFactory.CreateScope();
            var moduleCatalog = catalogScope.ServiceProvider.GetRequiredService<IModuleCatalogService>();
            var allModules = await moduleCatalog.ListAsync(configuration.ServerType, cancellationToken);
            var results = new List<ModuleCheckItemDto>();
            var compileIds = new List<string>();

            foreach (var moduleId in configuration.ModuleIds)
            {
                var module = allModules.FirstOrDefault(m => m.Id.Equals(moduleId, StringComparison.OrdinalIgnoreCase));
                var moduleDir = ModuleCompileEnvironment.ModuleDirectory(
                    Path.Combine(repoPath, "modules"),
                    module ?? new ModuleDto { Id = moduleId });
                var item = new ModuleCheckItemDto
                {
                    ModuleId = moduleId,
                    Name = module?.Name ?? moduleId,
                    Branch = module is null
                        ? null
                        : ModuleBranchResolver.Resolve(module, configuration.ModuleBranches, configuration.ModuleIds, allModules),
                    CommitSha = Directory.Exists(moduleDir)
                        ? await TryGetCommitShaAsync(moduleDir, cancellationToken)
                        : null,
                    Status = "pending",
                    CheckoutFolder = ModuleCompileEnvironment.CheckoutFolder(module, moduleId)
                };

                var hasSources = Directory.Exists(Path.Combine(moduleDir, "src"))
                    || File.Exists(Path.Combine(moduleDir, "CMakeLists.txt"));
                if (!hasSources)
                {
                    item.Status = "skipped";
                    await AddLogAsync(stackId, $"Skipping {item.Name}: no src/ or CMakeLists.txt.");
                    results.Add(item);
                    continue;
                }

                item.Status = "pending";
                compileIds.Add(moduleId);
                results.Add(item);
            }

            await PublishModuleResultsAsync(stackId, results);

            if (compileIds.Count == 0)
            {
                await AddLogAsync(stackId, "No compilable modules found.");
                await CompleteModuleCheckAsync(stackId, configuration, passed: true, results, cancellationToken);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            await AddLogAsync(
                stackId,
                "Compiling the shared 'modules' library (AzerothCore static modules do not have per-folder CMake targets)...");
            await UpdateBuildStatusAsync(
                stackId, BuildPhase.CheckingModules, 55, "Compiling selected modules...", null);

            var (ok, compileLog) = await StreamNinjaTargetAsync(
                compiler,
                volumeArgs,
                workDir,
                stackId,
                "modules",
                results,
                percentStart: 55,
                percentSpan: 25,
                progressLabel: "Compiling modules",
                cancellationToken);
            await PublishModuleResultsAsync(stackId, results);
            var errorsByModule = ModuleCheckCompiler.AttributeErrorsToModules(compileLog, results);
            if (!ok
                && errorsByModule.Count == 0
                && ModuleCheckCompiler.LooksLikeSuccessfulModulesLink(compileLog))
            {
                await AddLogAsync(
                    stackId,
                    "Ninja linked the modules library. Docker reported a wait error after that; treating the compile as successful.");
                ok = true;
            }

            if (!ok && errorsByModule.Count == 0)
            {
                var reason = ModuleCheckCompiler.LooksLikeDockerWaitFailure(compileLog)
                    ? "The compile container stopped unexpectedly (Docker lost the wait). This is not a compiler error in a specific module. Re-check modules."
                    : ModuleCheckCompiler.TrimError(compileLog, "The shared modules library failed to compile.");
                throw new InvalidOperationException(reason);
            }

            if (ok && errorsByModule.Count == 0)
            {
                await AddLogAsync(
                    stackId,
                    "Linking worldserver to catch missing symbols between modules (the static modules archive does not resolve them)...");
                await UpdateBuildStatusAsync(
                    stackId, BuildPhase.CheckingModules, 80, "Linking worldserver...", null);

                var (linkOk, linkLog) = await StreamNinjaTargetAsync(
                    compiler,
                    volumeArgs,
                    workDir,
                    stackId,
                    "worldserver",
                    results,
                    percentStart: 80,
                    percentSpan: 18,
                    progressLabel: "Linking worldserver",
                    cancellationToken);
                    compileLog = string.IsNullOrWhiteSpace(compileLog) ? linkLog : compileLog + "\n" + linkLog;
                    ok = linkOk;
                    errorsByModule = ModuleCheckCompiler.AttributeErrorsToModules(linkLog, results);
                if (!ok
                    && errorsByModule.Count == 0
                    && ModuleCheckCompiler.LooksLikeSuccessfulWorldserverLink(linkLog))
                {
                    await AddLogAsync(
                        stackId,
                        "Ninja linked worldserver. Docker reported a wait error after that; treating the link as successful.");
                    ok = true;
                }

                if (!ok && errorsByModule.Count == 0)
                {
                    var reason = ModuleCheckCompiler.LooksLikeDockerWaitFailure(linkLog)
                        ? "The compile container stopped unexpectedly (Docker lost the wait) while linking worldserver. Re-check modules."
                        : ModuleCheckCompiler.TrimError(
                            linkLog,
                            "worldserver failed to link. This is often a missing function between modules (the modules archive compiled, but the executable did not).");
                    throw new InvalidOperationException(reason);
                }
            }

            foreach (var item in results.Where(r => r.Status != "skipped"))
            {
                if (errorsByModule.TryGetValue(item.ModuleId, out var moduleError))
                {
                    item.Status = "failed";
                    item.Error = moduleError;
                    await AddLogAsync(stackId, $"ERROR: Module {item.Name} failed to compile.");
                    continue;
                }

                // Incremental ninja only prints folders it rebuilt. Unmentioned modules are still in the
                // shared library; leftover "pending" is not a failure.
                item.Status = "passed";
                item.Error = null;
            }

            await PublishModuleResultsAsync(stackId, results);

            var passed = results.All(item => item.Status is "passed" or "skipped");
            if (passed)
            {
                await AddLogAsync(stackId, "Selected modules compiled and linked into worldserver.");
            }

            await CompleteModuleCheckAsync(stackId, configuration, passed, results, cancellationToken);
            if (!passed)
            {
                var failedItems = results.Where(item => item.Status == "failed").ToList();
                var names = failedItems.Count == 0
                    ? "unknown module"
                    : string.Join(", ", failedItems.Select(item => item.Name));
                var excerpt = failedItems.Select(item => item.Error).FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));
                var message = $"Module compile check failed: {names}.";
                if (!string.IsNullOrWhiteSpace(excerpt))
                {
                    message = $"{message}\n\n{excerpt}";
                }

                throw new InvalidOperationException(message);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Module check cancelled for stack {StackId}", stackId);
            await FailBuildAsync(stackId, "Module check was cancelled by user", rollback: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Module check failed for stack {StackId}", stackId);
            if (BuildStates.TryGetValue(stackId, out var status) && string.IsNullOrEmpty(status.ErrorMessage))
            {
                await FailBuildAsync(stackId, ex.Message, rollback: false);
            }
            else
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
                var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId);
                if (stack is not null)
                {
                    stack.Status = StackStatus.Stopped;
                    await dbContext.SaveChangesAsync();
                }
            }

            await PersistFailedModuleCheckAsync(stackId);
        }
        finally
        {
            BuildCancellations.TryRemove(stackId, out _);
            await CleanupModuleCheckArtifactsAsync(stackId);
        }
    }

    private async Task CleanupModuleCheckArtifactsAsync(string stackId)
    {
        var repoPath = Path.Combine(_buildsPath, stackId, "azerothcore-wotlk");
        var removeImage = !BuildStates.Any(kv =>
            !kv.Key.Equals(stackId, StringComparison.OrdinalIgnoreCase)
            && kv.Value.CurrentPhase == BuildPhase.CheckingModules);
        try
        {
            var compiler = new ModuleCheckCompiler(_dockerOptions, _logger, stackId);
            await compiler.CleanupAfterCheckAsync(
                repoPath,
                removeImage,
                message => AddLogAsync(stackId, message),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Module-check cleanup failed for stack {StackId}", stackId);
        }
    }

    private async Task CompleteModuleCheckAsync(
        string stackId,
        StackConfigurationDto configuration,
        bool passed,
        List<ModuleCheckItemDto> results,
        CancellationToken cancellationToken)
    {
        var fingerprint = passed
            ? await ComputeCheckoutFingerprintAsync(stackId, configuration, cancellationToken)
            : string.Empty;
        var dto = new ModuleCheckStatusDto
        {
            Passed = passed,
            Skipped = false,
            Fingerprint = string.IsNullOrEmpty(fingerprint) ? null : fingerprint,
            CompletedAt = DateTime.UtcNow,
            Items = results
        };

        await PublishModuleResultsAsync(stackId, results);
        if (passed)
        {
            await UpdateBuildStatusAsync(
                stackId,
                BuildPhase.ModuleCheckPassed,
                100,
                "All selected modules compiled. You can now build Docker images.",
                null);
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is not null)
        {
            stack.ModuleCheckFingerprint = fingerprint;
            stack.ModuleCheckJson = JsonSerializer.Serialize(dto, JsonOptions);
            stack.Status = StackStatus.Stopped;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (passed)
        {
            await _eventPublisher.PublishBuildCompletedAsync(stackId, true);
        }
    }

    private async Task PersistFailedModuleCheckAsync(string stackId)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId);
        if (stack is null)
        {
            return;
        }

        var items = BuildStates.TryGetValue(stackId, out var status)
            ? status.ModuleResults
            : [];
        var dto = new ModuleCheckStatusDto
        {
            Passed = false,
            Skipped = false,
            Fingerprint = null,
            CompletedAt = DateTime.UtcNow,
            Items = items
        };
        stack.ModuleCheckFingerprint = string.Empty;
        stack.ModuleCheckJson = JsonSerializer.Serialize(dto, JsonOptions);
        await dbContext.SaveChangesAsync();
    }

    private async Task<(bool Ok, string CombinedLog)> StreamNinjaTargetAsync(
        ModuleCheckCompiler compiler,
        IReadOnlyList<string> volumeArgs,
        string workDir,
        string stackId,
        string target,
        List<ModuleCheckItemDto> results,
        int percentStart,
        int percentSpan,
        string progressLabel,
        CancellationToken cancellationToken)
    {
        var lastPublish = DateTime.MinValue;
        var lastProgress = DateTime.UtcNow;
        return await compiler.BuildTargetAsync(
            volumeArgs,
            workDir,
            target,
            async line =>
            {
                var changed = ModuleCheckCompiler.ApplyCompileLine(line, results);
                var isErrorLine = line.Contains(": error:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("fatal error:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("undefined reference", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("undefined symbol", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("FAILED:", StringComparison.Ordinal);
                if (ModuleCheckCompiler.TryParseNinjaProgress(line, out var current, out var total))
                {
                    var now = DateTime.UtcNow;
                    if (current == total || now - lastProgress >= TimeSpan.FromSeconds(1))
                    {
                        lastProgress = now;
                        var percent = percentStart + (int)(percentSpan * ((double)current / total));
                        await UpdateBuildStatusAsync(
                            stackId,
                            BuildPhase.CheckingModules,
                            Math.Min(99, percent),
                            $"{progressLabel} ({current}/{total})...",
                            null);
                    }

                    if (current == 1 || current == total || current % 20 == 0)
                    {
                        await AddLogAsync(stackId, line.Trim());
                    }
                }
                else if (isErrorLine)
                {
                    await AddLogAsync(stackId, line.Trim());
                }

                if (changed && (isErrorLine || DateTime.UtcNow - lastPublish >= TimeSpan.FromSeconds(1)))
                {
                    lastPublish = DateTime.UtcNow;
                    await PublishModuleResultsAsync(stackId, results);
                }
            },
            cancellationToken);
    }

    private async Task PublishModuleResultsAsync(string stackId, List<ModuleCheckItemDto> results)
    {
        if (!BuildStates.TryGetValue(stackId, out var buildStatus))
        {
            return;
        }

        buildStatus.ModuleResults = results;
        PersistBuildStatus(stackId, buildStatus);

        var snapshot = results.Select(item => new ModuleCheckItemDto
        {
            ModuleId = item.ModuleId,
            Name = item.Name,
            Status = item.Status,
            Error = item.Error,
            CommitSha = item.CommitSha,
            Branch = item.Branch
        }).ToList();
        try
        {
            await _eventPublisher.PublishModuleCheckUpdatedAsync(stackId, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish module-check results for stack {StackId}", stackId);
        }
    }

    private async Task ExecuteBuildAsync(
        string stackId,
        string stackName,
        StackConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        var postBuildAction = PostBuildAction.None;
        string? preUpdateRevisionId = null;

        try
        {
            _logger.LogInformation("Starting build for stack {StackId}", stackId);
            
            var buildPath = Path.Combine(_buildsPath, stackId);
            Directory.CreateDirectory(buildPath);
            _logger.LogInformation("Build path created: {BuildPath}", buildPath);

            // Scaffold the migration/patch directory structure (migrations/, server_dbc/, client/)
            // and seed the per-stack client settings templates so the launcher always gets a realmlist.wtf.
            Services.Patches.MigrationLayout.EnsureScaffold(buildPath, _migrationOptions.ClientSettingsTemplatePath);

            // Mark all directories as safe for git (avoids "dubious ownership" errors in Docker
            // where files may be owned by a different UID than the running process)
            await RunProcessAsync(stackId, "git", "config --global --add safe.directory *", buildPath, cancellationToken);

            // Determine repository URL and branch
            // For updates (configuration is null), use stored values from database if available
            // For new builds, use the configuration-based defaults
            string repoUrl;
            string branch;
            var configMigrationMode = ConfigMigrationMode.Skip;
            var isInitialImageBuild = true;
            
            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
                var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

                postBuildAction = stack?.PostBuildAction ?? PostBuildAction.None;
                configMigrationMode = stack?.ConfigMigrationMode ?? ConfigMigrationMode.Skip;
                isInitialImageBuild = stack is null || !HasCompletedWorldserverBuild(stack);

                if (stack is not null && !string.IsNullOrEmpty(stack.CoreRepositoryUrl))
                {
                    // Use stored repository info (handles imported stacks and updates)
                    repoUrl = stack.CoreRepositoryUrl;
                    branch = !string.IsNullOrEmpty(stack.CoreBranch) ? stack.CoreBranch : "master";
                    
                    _logger.LogInformation(
                        "Using stored repository info for stack {StackId}: {RepoUrl} @ {Branch}",
                        stackId, repoUrl, branch);
                }
                else
                {
                    // Fall back to the server-type catalog (new builds only). The catalog is the single,
                    // operator-editable source mapping a ServerType to its core repository + branch.
                    (repoUrl, branch) = _serverTypeCatalog.GetCoreRepository(configuration.ServerType);
                    
                    _logger.LogInformation(
                        "Using default repository for ServerType {ServerType}: {RepoUrl} @ {Branch}",
                        configuration.ServerType, repoUrl, branch);
                    
                    // Save repository info to database for future updates
                    if (stack is not null)
                    {
                        stack.CoreRepositoryUrl = repoUrl;
                        stack.CoreBranch = branch;
                        await dbContext.SaveChangesAsync(cancellationToken);
                    }
                }
            }

            // For an Update, snapshot the current databases + config BEFORE touching the code, so we
            // always have a rollback point. A snapshot failure aborts the update (via the outer catch).
            if (postBuildAction == PostBuildAction.SnapshotReapplyStart)
            {
                await UpdateBuildStatusAsync(stackId, BuildPhase.Cloning, 5, "Creating pre-update snapshot...", null);
                using var snapshotScope = _scopeFactory.CreateScope();
                var revisions = snapshotScope.ServiceProvider.GetRequiredService<IRevisionService>();
                var snapshot = await revisions.CreateAsync(stackId, "pre-update", cancellationToken);
                preUpdateRevisionId = snapshot.Id;
                await UpdateBuildStatusAsync(stackId, BuildPhase.Cloning, 5, "Tagging current server images as the restore checkpoint...", null);
                await revisions.PreserveCheckpointImagesAsync(stackId, snapshot.Id, cancellationToken);
                _logger.LogInformation(
                    "Pre-update snapshot {RevisionId} created for stack {StackId}", preUpdateRevisionId, stackId);
            }

            // Capture the operator's current server .conf before the code is touched, so the post-build
            // migration can merge old values into the freshly regenerated configs. Skipped when disabled.
            if (configMigrationMode != ConfigMigrationMode.Skip)
            {
                await UpdateBuildStatusAsync(stackId, BuildPhase.Cloning, 6, "Saving current server configuration...", null);
                using var captureScope = _scopeFactory.CreateScope();
                var configMigration = captureScope.ServiceProvider.GetRequiredService<IConfigMigrationService>();
                await configMigration.CaptureAsync(stackId, cancellationToken);
            }

            await CloneRepositoryAsync(stackId, buildPath, repoUrl, branch, configuration, cancellationToken,
                refreshIfPresent: !isInitialImageBuild);
            _logger.LogInformation("Repository cloned successfully for stack {StackId}", stackId);
            
            await PrepareModulesAsync(stackId, buildPath, configuration, cancellationToken,
                refreshIfPresent: !isInitialImageBuild);
            _logger.LogInformation("Modules prepared for stack {StackId}", stackId);

            var repoPath = Path.Combine(buildPath, "azerothcore-wotlk");
            await InjectModuleBuildPackagesAsync(stackId, repoPath, configuration, cancellationToken);
            
            await GenerateDockerComposeAsync(stackId, buildPath, configuration, cancellationToken);
            _logger.LogInformation("Docker Compose generated for stack {StackId}", stackId);
            
            await BuildDockerImagesAsync(stackId, buildPath, cancellationToken);
            await BuildLlmChatterBridgeImageAsync(stackId, buildPath, configuration, cancellationToken);
            _logger.LogInformation("Docker images ready for stack {StackId}", stackId);

            // Reconcile the operator's saved config with the newly built defaults (merge/fresh). Runs
            // after the images exist (their .conf.dist are the new base) and before the stack starts, so
            // the merged configs are what StackService seeds into the etc volume on the next start.
            if (configMigrationMode != ConfigMigrationMode.Skip)
            {
                await UpdateBuildStatusAsync(stackId, BuildPhase.Completed, 95, "Migrating server configuration...", null);
                using var migrateScope = _scopeFactory.CreateScope();
                var configMigration = migrateScope.ServiceProvider.GetRequiredService<IConfigMigrationService>();
                await configMigration.ApplyAsync(stackId, configMigrationMode, cancellationToken);
            }

            await CompleteBuildAsync(stackId);
            _logger.LogInformation("Build completed successfully for stack {StackId}", stackId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Build cancelled for stack {StackId}", stackId);
            await FailBuildAsync(stackId, "Build was cancelled by user", postBuildAction, preUpdateRevisionId, rollback: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Build failed for stack {StackId}", stackId);
            await FailBuildAsync(stackId, $"Build failed: {ex.Message}", postBuildAction, preUpdateRevisionId);
            throw; // Re-throw to ensure we see it in logs
        }
        finally
        {
            BuildCancellations.TryRemove(stackId, out _);
        }
    }

    private async Task CloneRepositoryAsync(
        string stackId, 
        string buildPath, 
        string repoUrl, 
        string branch, 
        StackConfigurationDto configuration, 
        CancellationToken cancellationToken,
        BuildPhase phase = BuildPhase.Cloning,
        bool refreshIfPresent = true)
    {
        await UpdateBuildStatusAsync(stackId, phase, 10, "Cloning AzerothCore repository...", null);

        var repoPath = Path.Combine(buildPath, "azerothcore-wotlk");

        if (Directory.Exists(repoPath))
        {
            // A previously interrupted clone/build (or a `git pull` on a shallow clone) can leave the
            // working tree incomplete - most visibly missing src/cmake/macros, which makes CMake fail
            // with "Unknown CMake command GetScriptModuleList". Only reuse a checkout we can verify is
            // intact; otherwise wipe it and re-clone from scratch.
            if (IsCoreCheckoutValid(repoPath))
            {
                if (refreshIfPresent)
                {
                    await AddLogAsync(stackId, "Repository already exists, refreshing to latest...");
                    var safeBranch = ModuleCatalogService.ValidateGitRef(branch);
                    await RunProcessArgsAsync(stackId, "git", new[] { "fetch", "--depth", "1", "origin", safeBranch }, repoPath, cancellationToken);
                    await RunProcessArgsAsync(stackId, "git", new[] { "reset", "--hard", "FETCH_HEAD" }, repoPath, cancellationToken);
                    await RunProcessArgsAsync(stackId, "git", new[] { "clean", "-ffd" }, repoPath, cancellationToken);
                }
                else
                {
                    await AddLogAsync(stackId, "Using the compile-checked AzerothCore checkout (not fetching latest).");
                }
            }
            else
            {
                await AddLogAsync(stackId, "Existing checkout is incomplete/corrupt; removing and re-cloning...");
                Directory.Delete(repoPath, recursive: true);
            }
        }

        if (!Directory.Exists(repoPath))
        {
            await AddLogAsync(stackId, $"Cloning {configuration.ServerType} AzerothCore repository from GitHub...");
            await AddLogAsync(stackId, $"Repository: {repoUrl} @ {branch}");
            var safeBranch = ModuleCatalogService.ValidateGitRef(branch);
            var safeRepo = ModuleCatalogService.ValidateGitRepository(repoUrl);
            await RunProcessArgsAsync(
                stackId,
                "git",
                new[] { "clone", "--depth", "1", "--branch", safeBranch, "--", safeRepo, "azerothcore-wotlk" },
                buildPath,
                cancellationToken);
        }

        // Guard against a clone that reported success but produced an unusable tree (e.g. partial
        // checkout, network hiccup). Fail fast with a clear message instead of a cryptic CMake error.
        if (!IsCoreCheckoutValid(repoPath))
        {
            throw new InvalidOperationException(
                "AzerothCore checkout is incomplete after clone (missing core CMake files such as " +
                "src/cmake/macros). This usually indicates a failed/partial git clone. Please retry the build.");
        }

        await StripBuildKitMountsAsync(stackId, repoPath, cancellationToken);

        await UpdateBuildStatusAsync(stackId, phase, 25, "Repository cloned successfully", null);
    }

    /// <summary>
    /// Adds module-specific -dev packages to AzerothCore's <c>apps/docker/Dockerfile</c> build stage
    /// so the later compose image build matches the module-check compiler image.
    /// </summary>
    private async Task InjectModuleBuildPackagesAsync(
        string stackId,
        string repoPath,
        StackConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var catalog = await scope.ServiceProvider
            .GetRequiredService<IModuleCatalogService>()
            .ListAsync(configuration.ServerType, cancellationToken);
        var packages = ModuleCompileEnvironment.ExtraAptPackagesFor(
            configuration.ModuleIds,
            catalog,
            Path.Combine(repoPath, "modules"));
        var dockerfile = Path.Combine(repoPath, "apps", "docker", "Dockerfile");
        if (!File.Exists(dockerfile))
        {
            if (packages.Count > 0)
            {
                await AddLogAsync(
                    stackId,
                    "AzerothCore apps/docker/Dockerfile was not found; extra module build packages were not injected.");
            }

            return;
        }

        var original = await File.ReadAllTextAsync(dockerfile, cancellationToken);
        var updated = ModuleCompileEnvironment.DisableExtractorTools(
            ModuleCompileEnvironment.InjectExtraBuildPackages(original, packages));
        if (string.Equals(original, updated, StringComparison.Ordinal))
        {
            return;
        }

        await File.WriteAllTextAsync(dockerfile, updated, cancellationToken);
        if (!string.Equals(original, ModuleCompileEnvironment.DisableExtractorTools(original), StringComparison.Ordinal))
        {
            await AddLogAsync(
                stackId,
                $"Disabled map/vmap/mmap extractor tools in the Docker compile (CTOOLS_BUILD={ModuleCompileEnvironment.StackToolsBuild}).");
        }
        if (packages.Count > 0)
        {
            await AddLogAsync(
                stackId,
                "Added extra build packages to apps/docker/Dockerfile: " + string.Join(", ", packages));
        }
    }

    // Matches BuildKit-only flags on RUN/COPY/ADD instructions. `--mount=` / `--chmod=` take a
    // space-free value token; `--link` is a boolean flag (with or without a value). `--chown` and
    // `--from` are supported by the classic builder and are handled contextually (see below).
    private static readonly Regex BuildKitFlagRegex = new(
        @"--(?:mount|chmod)=\S+|(?<=\s)--link(?:=\S+)?(?=\s|$)",
        RegexOptions.Compiled);

    // Matches a stage declaration and captures the base image/stage and optional stage name:
    // `FROM [--platform=...] <base> [AS <name>]`.
    private static readonly Regex FromStageRegex = new(
        @"^\s*FROM\s+(?:--\S+\s+)*(?<base>\S+)(?:\s+AS\s+(?<name>\S+))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A `--chown=` flag that resolves to the named DOCKER_USER account (as opposed to a numeric uid:gid).
    private static readonly Regex DockerUserChownRegex = new(
        @"\s--chown=\$\{?DOCKER_USER\}?(?::\$\{?DOCKER_USER\}?)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A `USER $DOCKER_USER` instruction (the account only exists in the runtime stage and its children).
    private static readonly Regex DockerUserInstructionRegex = new(
        @"^\s*USER\s+\$\{?DOCKER_USER\}?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Lines matching (adduser|useradd) together with DOCKER_USER create the acore account in a stage.
    private static readonly Regex UserCreationRegex = new(
        @"(?:adduser|useradd)\b.*DOCKER_USER",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // AzerothCore declares these identity ARGs in its `runtime` stage but relies on them in child stages
    // (`FROM runtime AS authserver|worldserver|db-import`) via `COPY --chown=$DOCKER_USER` and
    // `USER $DOCKER_USER`. ARG values do NOT cross a FROM boundary, so on the classic builder those
    // expand to empty and fail with "can't find uid for user :". Re-declaring them (with AzerothCore's own
    // defaults) after the FROM of every user-bearing stage brings them back into scope; a matching
    // --build-arg still wins.
    private static readonly string[] IdentityArgLines =
    {
        "ARG USER_ID=1000",
        "ARG GROUP_ID=1000",
        "ARG DOCKER_USER=acore",
    };

    /// <summary>
    /// Rewrites the cloned repo's Dockerfiles so the <em>classic</em> Docker builder can build them.
    /// Recent AzerothCore Dockerfiles assume BuildKit in ways this manager can't satisfy (it talks to the
    /// daemon through the docker-socket-proxy, which exposes the classic <c>/build</c> endpoint but not
    /// BuildKit's session/gRPC endpoints, so builds run with DOCKER_BUILDKIT=0):
    /// <list type="number">
    /// <item>BuildKit-only flags - <c>RUN --mount=type=cache/bind</c>, <c>COPY --chmod=</c>,
    /// <c>COPY --link</c> - which are stripped. Safe: the ccache mount is a speed-up, the .git bind mount
    /// only feeds the embedded revision string (AzerothCore degrades gracefully without .git), <c>--chmod</c>
    /// is superseded by the file's git mode bits, and <c>--link</c> only affects layer reuse.</item>
    /// <item>The <c>$DOCKER_USER</c> ARG referenced across FROM boundaries. In stages that <em>have</em>
    /// the <c>acore</c> account (the <c>runtime</c> stage - which runs <c>adduser</c> - and everything
    /// <c>FROM runtime</c>), the ARG is re-declared after FROM so <c>COPY --chown=$DOCKER_USER</c> /
    /// <c>USER $DOCKER_USER</c> resolve. In stages that do <em>not</em> create the account (e.g.
    /// <c>client-data</c>, which is <c>FROM skeleton</c>), those references can't resolve to a real user on
    /// the classic builder ("no such user: acore"), so the <c>--chown</c> is dropped and <c>USER</c> is
    /// removed - the step then runs as root, which is fine for an init container populating a volume that
    /// worldserver only reads.</item>
    /// </list>
    /// No-op when BuildKit is enabled.
    /// </summary>
    private async Task StripBuildKitMountsAsync(string stackId, string repoPath, CancellationToken cancellationToken)
    {
        // Only needed for the classic builder. When BuildKit is available, keep the cache/bind mounts.
        if (!string.Equals(Environment.GetEnvironmentVariable("DOCKER_BUILDKIT"), "0", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(repoPath, "Dockerfile*", SearchOption.AllDirectories))
        {
            var original = await File.ReadAllTextAsync(file, cancellationToken);
            var hasBuildKitFlag = BuildKitFlagRegex.IsMatch(original);
            var referencesDockerUser = original.Contains("DOCKER_USER", StringComparison.Ordinal);
            if (!hasBuildKitFlag && !referencesDockerUser)
            {
                continue;
            }

            var lines = original.Split('\n');

            // Pass 1: work out which stages have the acore account available. A stage "has user" if its
            // base stage has it OR the stage body itself creates it (adduser/useradd … DOCKER_USER).
            var stageHasUser = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string? currentStage = null;
            foreach (var line in lines)
            {
                var from = FromStageRegex.Match(line);
                if (from.Success)
                {
                    var baseName = from.Groups["base"].Value;
                    var inherited = stageHasUser.TryGetValue(baseName, out var baseHas) && baseHas;
                    currentStage = from.Groups["name"].Success ? from.Groups["name"].Value : null;
                    if (currentStage is not null)
                    {
                        stageHasUser[currentStage] = inherited;
                    }
                }
                else if (currentStage is not null && UserCreationRegex.IsMatch(line))
                {
                    stageHasUser[currentStage] = true;
                }
            }

            // Pass 2: rewrite. Strip BuildKit flags everywhere; re-scope $DOCKER_USER per stage lineage.
            var rebuilt = new List<string>(lines.Length + 16);
            var stageHasUserNow = false;
            foreach (var rawLine in lines)
            {
                var line = rawLine;
                if (BuildKitFlagRegex.IsMatch(line))
                {
                    var stripped = BuildKitFlagRegex.Replace(line, string.Empty);
                    // Collapse whitespace left where the flag was, preserving any trailing " \" continuation.
                    line = Regex.Replace(stripped, @"[ \t]{2,}", " ");
                }

                var from = FromStageRegex.Match(line);
                if (from.Success)
                {
                    var stageName = from.Groups["name"].Success ? from.Groups["name"].Value : null;
                    stageHasUserNow = stageName is not null && stageHasUser.TryGetValue(stageName, out var has) && has;
                    rebuilt.Add(line);
                    // Only user-bearing stages need $DOCKER_USER in scope for --chown/USER.
                    if (stageHasUserNow)
                    {
                        rebuilt.AddRange(IdentityArgLines);
                    }
                    continue;
                }

                if (!stageHasUserNow)
                {
                    // No acore account in this stage: a named-user --chown/USER can't resolve on the
                    // classic builder. Drop the chown (copy as root) and skip the USER instruction.
                    if (DockerUserInstructionRegex.IsMatch(line))
                    {
                        continue;
                    }

                    if (DockerUserChownRegex.IsMatch(line))
                    {
                        line = DockerUserChownRegex.Replace(line, string.Empty);
                    }
                }

                rebuilt.Add(line);
            }

            await File.WriteAllTextAsync(file, string.Join('\n', rebuilt), cancellationToken);
            await AddLogAsync(
                stackId,
                $"Adjusted {Path.GetFileName(file)} for the classic Docker builder " +
                "(stripped BuildKit-only flags; re-scoped $DOCKER_USER by stage lineage).");
        }
    }

    /// <summary>
    /// Verifies a cloned AzerothCore tree has the core build-system files. A missing
    /// <c>src/cmake/macros</c> (which defines <c>GetScriptModuleList</c>) is the classic cause of the
    /// "Unknown CMake command GetScriptModuleList" build failure, so we treat that as invalid.
    /// </summary>
    private static bool IsCoreCheckoutValid(string repoPath)
    {
        var cmakeLists = Path.Combine(repoPath, "CMakeLists.txt");
        var macrosDir = Path.Combine(repoPath, "src", "cmake", "macros");
        var distConfig = Path.Combine(repoPath, "conf", "dist", "config.cmake");

        return File.Exists(cmakeLists)
            && File.Exists(distConfig)
            && Directory.Exists(macrosDir)
            && Directory.EnumerateFiles(macrosDir, "*.cmake").Any();
    }

    private async Task PrepareModulesAsync(
        string stackId,
        string buildPath,
        StackConfigurationDto configuration,
        CancellationToken cancellationToken,
        BuildPhase phase = BuildPhase.PreparingModules,
        bool refreshIfPresent = true)
    {
        await UpdateBuildStatusAsync(stackId, phase, 30, "Preparing modules...", null);

        var modulesPath = Path.Combine(buildPath, "azerothcore-wotlk", "modules");
        Directory.CreateDirectory(modulesPath);

        using var scope = _scopeFactory.CreateScope();
        var moduleCatalog = scope.ServiceProvider.GetRequiredService<IModuleCatalogService>();
        var packageStorage = scope.ServiceProvider.GetRequiredService<IModulePackageStorage>();
        var allModules = await moduleCatalog.ListAsync(configuration.ServerType, cancellationToken);
        var keepIds = ModuleCompileEnvironment.ModuleDirectoriesToKeep(configuration.ModuleIds, allModules);
        RemoveUnselectedModuleDirectories(modulesPath, keepIds);

        if (configuration.ModuleIds.Count == 0)
        {
            await AddLogAsync(stackId, "No modules selected, skipping module preparation");
            await UpdateBuildStatusAsync(stackId, phase, 40, "Modules prepared", null);
            return;
        }

        await AddLogAsync(stackId, $"Integrating {configuration.ModuleIds.Count} module(s)...");

        foreach (var moduleId in configuration.ModuleIds)
        {
            var module = allModules.FirstOrDefault(m => m.Id == moduleId);
            if (module == null)
            {
                await AddLogAsync(stackId, $"Warning: Module {moduleId} not found in catalog, skipping");
                continue;
            }

            var resolved = ModuleBranchResolver.WithBranch(
                module,
                ModuleBranchResolver.Resolve(module, configuration.ModuleBranches, configuration.ModuleIds, allModules));
            var moduleDir = ModuleCompileEnvironment.ModuleDirectory(modulesPath, resolved);
            var checkoutFolder = ModuleCompileEnvironment.CheckoutFolder(resolved);
            if (!checkoutFolder.Equals(moduleId, StringComparison.OrdinalIgnoreCase))
            {
                await AddLogAsync(
                    stackId,
                    $"{resolved.Name} is checked out as modules/{checkoutFolder} so AzerothCore generates Add*Scripts() from the folder name.");
            }

            var pin = ModuleCompileEnvironment.RequiredBranchFor(
                moduleId, configuration.ModuleIds, allModules);
            if (!string.IsNullOrWhiteSpace(pin)
                && resolved.Branch.Equals(pin, StringComparison.OrdinalIgnoreCase))
            {
                await AddLogAsync(
                    stackId,
                    $"Pinned {resolved.Name} to branch {resolved.Branch} because another selected module requires it.");
            }

            if (resolved.SourceType == ModuleSource.Package)
            {
                // Uploaded package: copy the stored source tree into the build (replacing any prior copy).
                await AddLogAsync(stackId, $"Copying uploaded package module: {resolved.Name}");
                if (Directory.Exists(moduleDir))
                {
                    Directory.Delete(moduleDir, recursive: true);
                }
                await packageStorage.CopyToAsync(moduleId, moduleDir, cancellationToken);
            }
            else if (Directory.Exists(moduleDir) && IsGitCheckout(moduleDir))
            {
                if (!await GitOriginMatchesAsync(moduleDir, resolved.Repository, cancellationToken))
                {
                    await AddLogAsync(
                        stackId,
                        $"Module {resolved.Name} checkout is a different repository; removing and re-cloning...");
                    Directory.Delete(moduleDir, recursive: true);
                    await CloneGitModuleAsync(modulesPath, resolved, cancellationToken, stackId);
                }
                else if (refreshIfPresent)
                {
                    await AddLogAsync(stackId, $"Module {resolved.Name} already exists, pulling latest {resolved.Branch}...");
                    await ResetGitCheckoutToOriginAsync(moduleDir, resolved.Branch, cancellationToken, stackId);
                }
                else
                {
                    await AddLogAsync(stackId, $"Using compile-checked checkout for {resolved.Name}.");
                }
            }
            else
            {
                if (Directory.Exists(moduleDir))
                {
                    await AddLogAsync(stackId, $"Module {resolved.Name} checkout is not a git repo; removing and re-cloning...");
                    Directory.Delete(moduleDir, recursive: true);
                }

                await AddLogAsync(stackId, $"Cloning module: {resolved.Name} ({resolved.Branch})");
                await CloneGitModuleAsync(modulesPath, resolved, cancellationToken, stackId);
            }

            if (Directory.Exists(moduleDir) && resolved.SourceType != ModuleSource.Package)
            {
                var includeFix = ModuleCompileEnvironment.FixCaseMismatchedIncludes(moduleDir);
                if (!string.IsNullOrEmpty(includeFix))
                {
                    await AddLogAsync(stackId, includeFix);
                }
            }
        }

        foreach (var companion in ModuleCompileEnvironment.CompanionsFor(configuration.ModuleIds, allModules))
        {
            if (configuration.ModuleIds.Contains(companion.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolved = ModuleCompileEnvironment.ToModuleDto(companion);
            var moduleDir = Path.Combine(modulesPath, companion.Id);
            if (Directory.Exists(moduleDir) && IsGitCheckout(moduleDir))
            {
                if (refreshIfPresent)
                {
                    await AddLogAsync(stackId, $"Updating compile companion {resolved.Name} ({resolved.Branch})...");
                    await ResetGitCheckoutToOriginAsync(moduleDir, resolved.Branch, cancellationToken, stackId);
                }
                else
                {
                    await AddLogAsync(stackId, $"Using compile-checked checkout for {resolved.Name}.");
                }
            }
            else
            {
                if (Directory.Exists(moduleDir))
                {
                    Directory.Delete(moduleDir, recursive: true);
                }

                await AddLogAsync(stackId, $"Cloning compile companion: {resolved.Name} ({resolved.Branch})");
                await CloneGitModuleAsync(modulesPath, resolved, cancellationToken, stackId);
            }

            if (Directory.Exists(moduleDir))
            {
                var includeFix = ModuleCompileEnvironment.FixCaseMismatchedIncludes(moduleDir);
                if (!string.IsNullOrEmpty(includeFix))
                {
                    await AddLogAsync(stackId, includeFix);
                }
            }
        }

        RemoveUnselectedModuleDirectories(modulesPath, keepIds);
        var hooks = scope.ServiceProvider.GetRequiredService<IModuleInstallHookRunner>();
        var sqlRewritten = hooks.PrepareCheckouts(modulesPath);
        if (sqlRewritten.Count > 0)
        {
            await AddLogAsync(
                stackId,
                $"Rewrote {sqlRewritten.Count} module SQL file(s) via install hooks so db-import can apply them.");
        }

        await UpdateBuildStatusAsync(stackId, phase, 40, "Modules prepared", null);
    }

    internal static void RemoveUnselectedModuleDirectories(string modulesPath, IReadOnlyCollection<string> selectedIds)
    {
        if (!Directory.Exists(modulesPath))
        {
            return;
        }

        var selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(modulesPath))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
            {
                continue;
            }

            if (!selected.Contains(name))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private async Task GenerateDockerComposeAsync(
        string stackId,
        string buildPath,
        StackConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        await UpdateBuildStatusAsync(stackId, BuildPhase.Building, 45, "Generating Docker Compose configuration...", null);

        // AzerothCore already has docker-compose.yml, we create override and .env
        var repoPath = Path.Combine(buildPath, "azerothcore-wotlk");
        var overridePath = Path.Combine(repoPath, "docker-compose.override.yml");
        var envPath = Path.Combine(repoPath, ".env");
        
        // Create .env file with configuration (use stackId as unique tag)
        var envContent = GenerateEnvContent(stackId, configuration);
        await File.WriteAllTextAsync(envPath, envContent, cancellationToken);
        await AddLogAsync(stackId, "Environment configuration created");

        // Create docker-compose.override.yml for custom settings. Only matters at runtime (StackService
        // regenerates it before every start); all per-stack data is seeded into named volumes.
        var luaDir = Services.Patches.MigrationLayout.LuaScriptsDir(buildPath);
        var overrideContent = await GenerateDockerComposeOverrideAsync(
            stackId, configuration, Directory.Exists(luaDir), cancellationToken);
        await File.WriteAllTextAsync(overridePath, overrideContent, cancellationToken);
        await AddLogAsync(stackId, "Docker Compose override created");

        await UpdateBuildStatusAsync(stackId, BuildPhase.Building, 50, "Configuration generated", null);
    }

    private string GenerateEnvContent(string stackId, StackConfigurationDto config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AzerothCore Environment Configuration");

        // Host-interface binding policy (mirrors StackService runtime config so a stack started straight
        // from a build dir isn't briefly exposed on all interfaces before the runtime .env is regenerated):
        //  - DB / SOAP are data-plane only  -> DataPlaneBindAddress (loopback by default).
        //  - auth / world are the game protocol the client dials directly -> all interfaces (no prefix).
        // This method only generates local-stack builds; external stacks publish on all interfaces and are
        // handled by the runtime path.
        var dataBind = string.IsNullOrWhiteSpace(_dockerOptions.DataPlaneBindAddress)
            ? "127.0.0.1:"
            : _dockerOptions.DataPlaneBindAddress.Trim() + ":";
        var gameBind = config.ServerType == ServerType.Express ? "127.0.0.1:" : string.Empty;

        // Quote password to handle special characters
        sb.AppendLine($"DOCKER_DB_ROOT_PASSWORD=\"{config.Database.RootPassword}\"");
        sb.AppendLine($"DOCKER_DB_EXTERNAL_PORT={dataBind}{config.Database.Port}");
        sb.AppendLine($"DOCKER_WORLD_EXTERNAL_PORT={gameBind}{config.Ports.WorldServer}");
        sb.AppendLine($"DOCKER_SOAP_EXTERNAL_PORT={dataBind}{config.Ports.SoapPort}");
        
        // Auth server port - need to override in docker-compose.override.yml
        sb.AppendLine($"DOCKER_AUTH_EXTERNAL_PORT={gameBind}{config.Ports.AuthServer}");
        
        // Use stackId as unique image tag to avoid collision between stacks
        sb.AppendLine($"DOCKER_IMAGE_TAG={stackId}");
        sb.AppendLine($"COMPOSE_PROJECT_NAME={DockerComposeOverrideGenerator.GetComposeProjectName(stackId)}");
        
        // User/Group IDs for Podman/Docker
        sb.AppendLine("DOCKER_USER_ID=1000");
        sb.AppendLine("DOCKER_GROUP_ID=1000");
        sb.AppendLine("DOCKER_USER=acore");
        
        // Server config (etc) and logs are pre-seeded named volumes; the base compose references them via
        // these variables. The manager seeds/regenerates them before every start.
        sb.AppendLine($"DOCKER_VOL_LOGS={DockerComposeOverrideGenerator.LogsVolumeName(stackId)}");
        sb.AppendLine($"DOCKER_VOL_ETC={DockerComposeOverrideGenerator.EtcVolumeName(stackId)}");

        // Shared cmake build stage. dbimport is a tool; none would omit the binary the db-import
        // image copies. Extractors stay off because client-data already ships maps/vmaps/mmaps.
        sb.AppendLine($"CTOOLS_BUILD={ModuleCompileEnvironment.StackToolsBuild}");

        return sb.ToString();
    }

    private async Task<string> GenerateDockerComposeOverrideAsync(
        string stackId,
        StackConfigurationDto config,
        bool includeLua,
        CancellationToken cancellationToken)
    {
        var serviceEnvironment = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (serviceId, bucket) in config.Advanced.ServiceEnvVars ?? new())
        {
            if (!string.IsNullOrWhiteSpace(serviceId))
            {
                serviceEnvironment[serviceId] = bucket ?? new Dictionary<string, string>();
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var catalog = await scope.ServiceProvider
            .GetRequiredService<IModuleCatalogService>()
            .ListAsync(config.ServerType, cancellationToken);
        var runtimeSidecars = ModuleCompileEnvironment.RuntimeSidecarsFor(config.ModuleIds, catalog);
        var sidecar = ModuleCompileEnvironment.OllamaSidecarFor(runtimeSidecars);
        var contextArg = await ResolveOllamaProbeContextArgAsync(stackId, scope, cancellationToken);
        var gpu = sidecar is null
            ? GpuBackend.Cpu
            : await OllamaGpuProbe.ProbeAsync(contextArg, _logger, cancellationToken);
        var ollama = sidecar is null ? null : OllamaComposeOptions.FromSidecar(sidecar, gpu);

        string? managerDataVolumeName = null;
        string? modulesSubpath = null;
        var modulesDir = Path.Combine(_buildsPath, stackId, "azerothcore-wotlk", "modules");
        if (!string.IsNullOrWhiteSpace(_dockerOptions.DataVolumeName)
            && Directory.Exists(modulesDir)
            && DockerComposeOverrideGenerator.TryGetDataVolumeSubpath(
                modulesDir, _dockerOptions.BuildsPath, out var relative)
            && !string.IsNullOrEmpty(relative))
        {
            managerDataVolumeName = _dockerOptions.DataVolumeName.Trim();
            modulesSubpath = relative;
        }

        return DockerComposeOverrideGenerator.Generate(
            stackId,
            config.StackName,
            serviceEnvironment,
            includeLua,
            ollama: ollama,
            managerDataVolumeName: managerDataVolumeName,
            modulesSubpath: modulesSubpath,
            llmChatterBridge: ModuleCompileEnvironment.HasLlmChatterBridge(runtimeSidecars)
                ? LlmChatterBridgeComposeOptions.ForStack(stackId)
                : null);
    }

    /// <summary>
    /// Docker CLI context prefix for the stack's engine so the Ollama GPU probe sees the same
    /// devices Start will use. Local stacks get an empty prefix; external stacks use
    /// <c>--context</c>. Probe failure here must not fail the build (CPU override is valid).
    /// </summary>
    private async Task<string> ResolveOllamaProbeContextArgAsync(
        string stackId,
        IServiceScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            var stack = await dbContext.ManagedStacks
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
            if (stack is null)
            {
                return string.Empty;
            }

            var remote = scope.ServiceProvider.GetRequiredService<IRemoteEngineService>();
            return await remote.ContextArgAsync(stack, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve Docker context for Ollama GPU probe on stack {StackId}.", stackId);
            return string.Empty;
        }
    }

    /// <summary>
    /// Forces Compose to build one service at a time. All four AzerothCore images come from the same
    /// Dockerfile and share its cmake stage, and the classic builder (which the socket proxy pins us to)
    /// has no cross-build coordination: run in parallel, the services each compile the core in full and
    /// race over the shared stage's untagged images, so a later stage can resolve to an id the daemon has
    /// already dropped - <c>No such image: sha256:…</c>. Serialised, the first build populates the cache
    /// the rest hit. The variable is read by both Compose v1 and v2 and ignored elsewhere.
    /// </summary>
    private static readonly Dictionary<string, string> SerialComposeBuildEnvironment =
        new() { ["COMPOSE_PARALLEL_LIMIT"] = "1" };

    private async Task BuildDockerImagesAsync(string stackId, string buildPath, CancellationToken cancellationToken)
    {
        await UpdateBuildStatusAsync(stackId, BuildPhase.CreatingImages, 60, "Building Docker images...", null);
        await AddLogAsync(stackId, "Starting Docker build process (this may take several minutes)...");

        var repoPath = Path.Combine(buildPath, "azerothcore-wotlk");

        // Build using Docker Compose in the azerothcore-wotlk directory
        await AddLogAsync(stackId, "Building AzerothCore from source using Docker Compose...");
        await AddLogAsync(stackId, "This will take 15-30 minutes on first build (compiling C++ code)...");
        
        try
        {
            await UpdateBuildStatusAsync(stackId, BuildPhase.CreatingImages, 65, "Building images from source...", null);

            // Get the appropriate docker compose command
            var (command, argPrefix) = DockerComposeHelper.GetDockerCompose(_dockerOptions.ComposeCommand);
            // Classic-builder RUN containers stay behind after a failed or cancelled compile unless
            // --force-rm is set. buildx_buildkit_default is a BuildKit builder and is not created here.
            var composeArgs = string.IsNullOrEmpty(argPrefix)
                ? $"build --force-rm --build-arg CTOOLS_BUILD={ModuleCompileEnvironment.StackToolsBuild}"
                : $"{argPrefix} build --force-rm --build-arg CTOOLS_BUILD={ModuleCompileEnvironment.StackToolsBuild}";

            // Build the default (non-profiled) services: db-import, worldserver, authserver, and
            // client-data. `ac-client-data-init` (target client-data) IS required - it populates the
            // stack's `_ac-client-data` volume that the manager's dbc/maps migration pipeline reads (see
            // MigrationService.Apply) and that worldserver mounts. `ac-tools` / `ac-dev-server` carry
            // compose `profiles:` so a bare `docker compose build` skips those images. CTOOLS_BUILD
            // is db-only so dbimport is still produced and map/vmap/mmap extractors are not.
            await RunProcessAsync(
                stackId,
                command,
                composeArgs,
                repoPath, // Run from the repo directory where docker-compose.yml is
                cancellationToken,
                SerialComposeBuildEnvironment);

            await UpdateBuildStatusAsync(stackId, BuildPhase.CreatingImages, 95, "All images built successfully", null);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exited with code"))
        {
            // Check if images were actually created despite non-zero exit code
            // podman-compose can return 125 even on successful builds
            await AddLogAsync(stackId, $"Build process exited with non-zero code, verifying images...");
            
            var imagesExist = await VerifyImagesExistAsync(stackId, repoPath, cancellationToken);
            if (imagesExist)
            {
                await AddLogAsync(stackId, "Images verified successfully - build completed despite exit code");
                await UpdateBuildStatusAsync(stackId, BuildPhase.CreatingImages, 95, "All images built successfully", null);
            }
            else
            {
                throw new InvalidOperationException($"Failed to build Docker images: {ex.Message}", ex);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to build Docker images: {ex.Message}", ex);
        }
        finally
        {
            await RemoveClassicBuilderLeftoversAsync(stackId, CancellationToken.None);
        }

        await AddLogAsync(stackId, "All Docker images are ready");
    }

    /// <summary>
    /// Bakes LLM Chatter's Python bridge into a per-stack image. The Dockerfile is written to the
    /// build directory rather than the module checkout so the checkout stays a clean clone, and is
    /// passed with <c>-f</c> because Docker allows a Dockerfile outside the build context.
    /// </summary>
    private async Task BuildLlmChatterBridgeImageAsync(
        string stackId,
        string buildPath,
        StackConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        if (configuration.ModuleIds?.Contains(LlmChatterBridge.ModuleId, StringComparer.OrdinalIgnoreCase) != true)
        {
            return;
        }

        var context = Path.Combine(buildPath, "azerothcore-wotlk", "modules", LlmChatterBridge.CheckoutFolder);
        if (!Directory.Exists(Path.Combine(context, "tools")))
        {
            await AddLogAsync(
                stackId,
                $"LLM Chatter bridge sources not found at {context}/tools; skipping the bridge image. "
                + "Bots will queue chatter that nothing delivers until this is rebuilt.");
            return;
        }

        var dockerfile = Path.Combine(buildPath, "llm-chatter-bridge.Dockerfile");
        await File.WriteAllTextAsync(dockerfile, LlmChatterBridge.DockerfileContent, cancellationToken);

        var image = LlmChatterBridge.ImageTag(stackId);
        await AddLogAsync(stackId, $"Building the LLM Chatter bridge image ({image})...");

        var (command, _) = DockerComposeHelper.GetDockerCompose(_dockerOptions.ComposeCommand);
        var engine = command.Contains("podman", StringComparison.OrdinalIgnoreCase) ? "podman" : "docker";
        await RunProcessArgsAsync(
            stackId,
            engine,
            ["build", "--force-rm", "-t", image, "-f", dockerfile, context],
            buildPath,
            cancellationToken);

        await AddLogAsync(stackId, "LLM Chatter bridge image built.");
    }

    /// <summary>
    /// Removes exited classic-builder compile leftovers (<c>cmake /azerothcore</c> RUN containers).
    /// Running compile containers are left alone so a concurrent stack build is not killed.
    /// <c>buildx_buildkit_default</c> is never matched.
    /// </summary>
    private async Task RemoveClassicBuilderLeftoversAsync(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            var engine = File.Exists("/usr/bin/podman") ? "podman" : "docker";
            var list = new ProcessStartInfo
            {
                FileName = engine,
                Arguments = $"ps -a --filter status=exited --no-trunc --format \"{ClassicBuilderLeftovers.DockerPsFormat}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var listProcess = Process.Start(list);
            if (listProcess is null)
            {
                return;
            }

            var stdout = await listProcess.StandardOutput.ReadToEndAsync(cancellationToken);
            await listProcess.WaitForExitAsync(cancellationToken);
            if (listProcess.ExitCode != 0)
            {
                return;
            }

            var ids = ClassicBuilderLeftovers.IdsToRemove(stdout);
            if (ids.Count == 0)
            {
                return;
            }

            var rm = new ProcessStartInfo
            {
                FileName = engine,
                Arguments = "rm " + string.Join(' ', ids),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var rmProcess = Process.Start(rm);
            if (rmProcess is null)
            {
                return;
            }

            await rmProcess.WaitForExitAsync(cancellationToken);
            if (rmProcess.ExitCode == 0)
            {
                await AddLogAsync(stackId, $"Removed {ids.Count} leftover compile container(s) from the image build.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove classic-builder leftover containers after stack {StackId} image build.", stackId);
        }
    }

    private async Task<bool> VerifyImagesExistAsync(string stackId, string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            // Check if the main AzerothCore images exist
            // Try podman first (Fedora), fallback to docker
            var dockerCommand = File.Exists("/usr/bin/podman") ? "podman" : "docker";
            
            // Images are tagged with stackId for isolation. client-data is an init image (populates the
            // shared data volume worldserver reads), so it must build too - verify all four.
            var imageNames = new[]
            {
                $"localhost/acore/ac-wotlk-worldserver:{stackId}",
                $"localhost/acore/ac-wotlk-authserver:{stackId}",
                $"localhost/acore/ac-wotlk-db-import:{stackId}",
                $"localhost/acore/ac-wotlk-client-data:{stackId}"
            };

            var foundCount = 0;
            foreach (var imageName in imageNames)
            {
                var verifyProcess = new ProcessStartInfo
                {
                    FileName = dockerCommand,
                    Arguments = $"images -q {imageName}",
                    WorkingDirectory = repoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(verifyProcess);
                if (process == null) continue;

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(output))
                {
                    foundCount++;
                }
            }

            var expected = imageNames.Length;
            var allExist = foundCount >= expected;
            await AddLogAsync(stackId, $"Image verification: found {foundCount}/{expected} images");
            return allExist;
        }
        catch (Exception ex)
        {
            await AddLogAsync(stackId, $"Failed to verify images: {ex.Message}");
            return false;
        }
    }

    private async Task CompleteBuildAsync(string stackId)
    {
        await UpdateBuildStatusAsync(stackId, BuildPhase.Completed, 100, "Build completed successfully!", null);
        
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        
        var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId);
        var postBuildAction = PostBuildAction.None;
        if (stack is not null)
        {
            stack.Status = StackStatus.Stopped;
            postBuildAction = stack.PostBuildAction;
            stack.PostBuildAction = PostBuildAction.None;
            // The migration was already applied above; reset so a later plain rebuild doesn't repeat it.
            stack.ConfigMigrationMode = ConfigMigrationMode.Skip;
            
            // Capture version information (commit SHAs) for update tracking
            try
            {
                var buildPath = Path.Combine(_buildsPath, stackId);
                var repoPath = Path.Combine(buildPath, "azerothcore-wotlk");
                
                // Capture core repository SHA
                if (Directory.Exists(repoPath))
                {
                    stack.CoreCommitSha = await GetCurrentCommitShaAsync(repoPath, CancellationToken.None);
                    stack.LastBuiltAt = DateTime.UtcNow;
                    _logger.LogInformation("Captured core commit SHA {Sha} for stack {StackId}", 
                        stack.CoreCommitSha, stackId);
                }
                
                // Capture module SHAs
                var moduleVersions = new List<ModuleVersionInfo>();
                var modulesPath = Path.Combine(repoPath, "modules");
                
                if (Directory.Exists(modulesPath))
                {
                    var moduleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson) ?? [];
                    var moduleCatalog = scope.ServiceProvider.GetRequiredService<IModuleCatalogService>();
                    var allModules = await moduleCatalog.ListAsync(stack.ServerType, CancellationToken.None);
                    
                    foreach (var moduleId in moduleIds)
                    {
                        var module = allModules.FirstOrDefault(m => m.Id == moduleId);
                        var modulePath = ModuleCompileEnvironment.ModuleDirectory(
                            modulesPath,
                            module ?? new ModuleDto { Id = moduleId });
                        if (Directory.Exists(modulePath))
                        {
                            // Package modules aren't git repos, so there's no remote SHA to track.
                            if (module != null && module.SourceType != ModuleSource.Package)
                            {
                                var sha = await GetCurrentCommitShaAsync(modulePath, CancellationToken.None);
                                moduleVersions.Add(new ModuleVersionInfo
                                {
                                    ModuleId = moduleId,
                                    CommitSha = sha,
                                    Repository = module.Repository,
                                    Branch = module.Branch
                                });
                                _logger.LogInformation("Captured module {ModuleId} commit SHA {Sha}", 
                                    moduleId, sha);
                            }
                        }
                    }
                }
                
                stack.ModuleVersionsJson = JsonSerializer.Serialize(moduleVersions);
                
                // Clear update flags since we just built the latest version
                stack.IsOutdated = false;
                stack.IsCoreOutdated = false;
                stack.OutdatedModuleCount = 0;
                stack.LatestAvailableCoreSha = stack.CoreCommitSha;
                stack.OutdatedModulesJson = "[]";
                stack.LastUpdateCheckAt = DateTime.UtcNow;
                
                _logger.LogInformation("Cleared update flags for stack {StackId} after successful build", stackId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture version information for stack {StackId}", stackId);
                // Don't fail the build if version capture fails
            }
            
            await dbContext.SaveChangesAsync();
        }

        // External stacks build locally, then ship the freshly built images to the remote engine so a
        // subsequent `docker --context ... compose up` uses them without rebuilding on the remote.
        if (stack is not null && stack.DeploymentTarget == DeploymentTarget.External)
        {
            await ShipImagesToRemoteAsync(stack);
        }

        await _eventPublisher.PublishBuildCompletedAsync(stackId, true);

        // For an Update: reapply every applied patch's SQL on top of the fresh standard updates, then
        // boot the stack. Plain rebuilds/initial builds leave the stack Stopped (unchanged behavior).
        if (postBuildAction == PostBuildAction.SnapshotReapplyStart)
        {
            await CompleteUpdateAsync(stackId, stack?.AppliedPatchLevel ?? 0);
        }
        else if (stack is { ServerType: ServerType.Express, ExpressProvisionStatus: ExpressProvisionStatus.Pending, DeploymentTarget: DeploymentTarget.Local })
        {
            stack.ExpressProvisionMessage = "First build finished. Click Setup and Launch! on Overview.";
            await dbContext.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Streams the locally-built stack images plus the shared armory + client-server images to the
    /// remote engine (best-effort, logged), so a subsequent remote <c>compose up</c> finds every image
    /// it needs and the external stack serves its own armory + client files.
    /// </summary>
    private async Task ShipImagesToRemoteAsync(ManagedStackEntity stack)
    {
        await AddLogAsync(stack.Id, "Shipping built images to remote engine...");
        await _stackImageShipping.ShipStackImagesAsync(stack, includeArmory: stack.IncludeArmory, includeClient: true, CancellationToken.None);
        await AddLogAsync(stack.Id, "Remote image shipping finished.");
    }

    /// <summary>
    /// Post-Update orchestration: reapply all patch SQL (if any patches are applied) or just start,
    /// then bring the stack fully up. Failures here are logged but leave the images built.
    /// </summary>
    private async Task CompleteUpdateAsync(string stackId, int appliedPatchLevel)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var migrations = scope.ServiceProvider.GetRequiredService<IMigrationService>();

            if (appliedPatchLevel > 0)
            {
                await UpdateBuildStatusAsync(
                    stackId, BuildPhase.Completed, 100, "Reapplying patches after update...", null);
                var result = await migrations.ReapplyAllAsync(stackId, CancellationToken.None);
                if (!result.Success)
                {
                    _logger.LogWarning(
                        "Reapply-all reported failure for stack {StackId}: {Message}", stackId, result.Error);
                }
            }
            else
            {
                await UpdateBuildStatusAsync(
                    stackId, BuildPhase.Completed, 100, "Applying module database updates...", null);
                var result = await migrations.ApplyStandardDbUpdatesAsync(stackId, CancellationToken.None);
                if (!result.Success)
                {
                    _logger.LogWarning(
                        "Standard DB updates reported failure for stack {StackId}: {Message}", stackId, result.Error);
                }
            }

            await UpdateBuildStatusAsync(stackId, BuildPhase.Completed, 100, "Starting server...", null);
            var stacks = scope.ServiceProvider.GetRequiredService<IStackService>();
            await stacks.StartAsync(stackId, CancellationToken.None);
            _logger.LogInformation("Update completed and stack {StackId} started", stackId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-update reapply/start failed for stack {StackId}", stackId);
        }
    }

    private async Task FailBuildAsync(
        string stackId,
        string errorMessage,
        PostBuildAction postBuildAction = PostBuildAction.None,
        string? preUpdateRevisionId = null,
        bool rollback = true)
    {
        string? rollbackMessage = null;

        if (rollback
            && postBuildAction == PostBuildAction.SnapshotReapplyStart
            && !string.IsNullOrEmpty(preUpdateRevisionId))
        {
            rollbackMessage = await TryRollbackFailedUpdateAsync(stackId, preUpdateRevisionId);
        }

        var finalMessage = rollbackMessage is null
            ? errorMessage
            : $"{errorMessage} {rollbackMessage}";

        if (BuildStates.TryGetValue(stackId, out var buildStatus))
        {
            buildStatus.CurrentPhase = BuildPhase.Failed;
            buildStatus.CurrentStep = rollbackMessage?.StartsWith("Automatic rollback", StringComparison.Ordinal) == true
                ? "Update failed - rolled back"
                : (finalMessage.Contains("odule compile check", StringComparison.OrdinalIgnoreCase)
                    ? "Module check failed"
                    : "Build failed");
            buildStatus.ErrorMessage = finalMessage;
            buildStatus.RecentLogs.Add($"ERROR: {errorMessage}");
            if (rollbackMessage is not null)
            {
                buildStatus.RecentLogs.Add(rollbackMessage);
            }
            PersistBuildStatus(stackId, buildStatus);

            await _eventPublisher.PublishBuildFailedAsync(stackId, finalMessage);
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        
        var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId);
        if (stack is not null)
        {
            stack.Status = StackStatus.Stopped;
            stack.PostBuildAction = PostBuildAction.None;
            stack.ConfigMigrationMode = ConfigMigrationMode.Skip;
            await dbContext.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Restores the pre-update snapshot after a failed update build so databases, config, and version
    /// metadata match the state before the update was attempted.
    /// </summary>
    private async Task<string?> TryRollbackFailedUpdateAsync(string stackId, string revisionId)
    {
        try
        {
            await UpdateBuildStatusAsync(
                stackId, BuildPhase.Failed, 0, "Update failed - rolling back to pre-update snapshot...", null);
            await AddLogAsync(stackId, $"Restoring pre-update snapshot {revisionId}...");

            using var scope = _scopeFactory.CreateScope();
            var revisions = scope.ServiceProvider.GetRequiredService<IRevisionService>();

            await revisions.RestoreAsync(stackId, revisionId, CancellationToken.None);

            _logger.LogInformation(
                "Automatic rollback to pre-update snapshot {RevisionId} completed for stack {StackId}",
                revisionId, stackId);

            return "Automatic rollback to the pre-update snapshot completed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Automatic rollback failed for stack {StackId} (revision {RevisionId})", stackId, revisionId);
            await AddLogAsync(stackId, $"Automatic rollback failed: {ex.Message}");
            return $"Automatic rollback failed: {ex.Message}";
        }
    }

    private async Task UpdateBuildStatusAsync(
        string stackId,
        BuildPhase phase,
        int progressPercent,
        string currentStep,
        string? logLine)
    {
        if (!BuildStates.TryGetValue(stackId, out var buildStatus))
        {
            return;
        }

        buildStatus.CurrentPhase = phase;
        buildStatus.ProgressPercent = progressPercent;
        buildStatus.CurrentStep = currentStep;

        if (logLine is not null)
        {
            buildStatus.RecentLogs.Add(logLine);
            if (buildStatus.RecentLogs.Count > 50)
            {
                buildStatus.RecentLogs.RemoveAt(0);
            }
        }

        await _eventPublisher.PublishPhaseChangedAsync(stackId, phase.ToString());
        await _eventPublisher.PublishProgressUpdatedAsync(stackId, progressPercent, currentStep);
        PersistBuildStatus(stackId, buildStatus);
    }

    private async Task AddLogAsync(string stackId, string logLine)
    {
        if (!BuildStates.TryGetValue(stackId, out var buildStatus))
        {
            _logger.LogWarning("Attempted to add log for non-existent build: {StackId}", stackId);
            return;
        }

        var timestampedLog = $"[{DateTime.UtcNow:HH:mm:ss}] {logLine}";
        buildStatus.RecentLogs.Add(timestampedLog);
        
        if (buildStatus.RecentLogs.Count > 50)
        {
            buildStatus.RecentLogs.RemoveAt(0);
        }

        PersistBuildStatus(stackId, buildStatus);

        _logger.LogInformation("Build {StackId}: {LogLine}", stackId, logLine);
        
        try
        {
            await _eventPublisher.PublishLogReceivedAsync(stackId, timestampedLog);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish log to SignalR for stack {StackId}", stackId);
        }
    }

    private Task RunProcessAsync(
        string stackId,
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ApplyFileName(startInfo, fileName);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        return RunProcessAsync(stackId, startInfo, $"{fileName} {arguments}", cancellationToken);
    }

    /// <summary>
    /// Runs a process passing each argument as a discrete token via <see cref="ProcessStartInfo.ArgumentList"/>.
    /// This avoids string-splitting/argument-injection: user-influenced values (git refs/URLs) are never
    /// concatenated into a single command line where a crafted token could masquerade as an option.
    /// </summary>
    private Task RunProcessArgsAsync(
        string stackId,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ApplyFileName(startInfo, fileName);
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return RunProcessAsync(stackId, startInfo, $"{fileName} {string.Join(' ', arguments)}", cancellationToken);
    }

    private async Task RunProcessAsync(
        string stackId,
        ProcessStartInfo startInfo,
        string displayCommand,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };

        // Use synchronous event handlers to avoid async void issues
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                // Fire and forget - don't await to avoid blocking the event handler
                _ = AddLogAsync(stackId, e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _ = AddLogAsync(stackId, $"STDERR: {e.Data}");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Process '{displayCommand}' exited with code {process.ExitCode}");
        }
    }

    public Task<BuildStatusDto?> GetStatusAsync(string stackId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BuildStates.TryGetValue(stackId, out var buildStatus))
        {
            return Task.FromResult<BuildStatusDto?>(buildStatus);
        }

        return Task.FromResult(LoadPersistedBuildStatus(stackId));
    }

    public async Task RecoverInterruptedBuildsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();

        var buildingStacks = await dbContext.ManagedStacks
            .Where(s => s.Status == StackStatus.Building)
            .ToListAsync(cancellationToken);

        foreach (var stack in buildingStacks)
        {
            if (BuildStates.ContainsKey(stack.Id))
            {
                continue;
            }

            var persisted = LoadPersistedBuildStatus(stack.Id);
            if (persisted is null || IsTerminalPhase(persisted.CurrentPhase))
            {
                stack.Status = StackStatus.Stopped;
                continue;
            }

            persisted.CurrentPhase = BuildPhase.Failed;
            persisted.CurrentStep = "Build interrupted";
            persisted.ErrorMessage =
                "The build was interrupted because the platform restarted while it was in progress. Recompile to try again.";
            persisted.RecentLogs.Add($"ERROR: {persisted.ErrorMessage}");
            PersistBuildStatus(stack.Id, persisted);
            stack.Status = StackStatus.Stopped;
            _logger.LogWarning(
                "Recovered interrupted build for stack {StackId} at phase {Phase}",
                stack.Id,
                persisted.CurrentPhase);
        }

        if (buildingStacks.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await RemoveClassicBuilderLeftoversAsync(buildingStacks[0].Id, CancellationToken.None);
        }
    }

    public async Task<bool> CancelAsync(string stackId, CancellationToken cancellationToken = default)
    {
        if (!BuildCancellations.TryGetValue(stackId, out var cts))
        {
            return false;
        }

        await AddLogAsync(stackId, "Cancellation requested...");
        cts.Cancel();
        
        return true;
    }

    public async Task<long> CleanupAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var buildPath = Path.Combine(_buildsPath, stackId);
        long freedSpace = 0;

        if (Directory.Exists(buildPath))
        {
            var dirInfo = new DirectoryInfo(buildPath);
            freedSpace = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
            
            Directory.Delete(buildPath, recursive: true);
            _logger.LogInformation("Cleaned up build directory for stack {StackId}, freed {FreedSpace} bytes", stackId, freedSpace);
        }

        BuildStates.TryRemove(stackId, out _);
        BuildCancellations.TryRemove(stackId, out _);

        var statusPath = BuildStatusPath(stackId);
        if (File.Exists(statusPath))
        {
            File.Delete(statusPath);
        }
        
        return freedSpace;
    }

    private static string BuildStatusPath(string buildsPath, string stackId) =>
        Path.Combine(buildsPath, stackId, BuildStatusFileName);

    private string BuildStatusPath(string stackId) => BuildStatusPath(_buildsPath, stackId);

    private void PersistBuildStatus(string stackId, BuildStatusDto status)
    {
        try
        {
            var path = BuildStatusPath(stackId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist build status for stack {StackId}", stackId);
        }
    }

    private BuildStatusDto? LoadPersistedBuildStatus(string stackId)
    {
        try
        {
            var path = BuildStatusPath(stackId);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<BuildStatusDto>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted build status for stack {StackId}", stackId);
            return null;
        }
    }
    
    /// <summary>
    /// Get the current commit SHA from a git repository
    /// </summary>
    private async Task<string> GetCurrentCommitShaAsync(string gitRepoPath, CancellationToken cancellationToken)
    {
        var sha = await TryGetCommitShaAsync(gitRepoPath, cancellationToken);
        return sha ?? throw new InvalidOperationException("Failed to get commit SHA: git rev-parse HEAD did not succeed.");
    }

    private static async Task<string?> TryGetCommitShaAsync(string gitRepoPath, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunGitCaptureAsync(
            ["rev-parse", "HEAD"],
            gitRepoPath,
            cancellationToken,
            TimeSpan.FromSeconds(15));
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }

    private static bool IsGitCheckout(string path)
    {
        var git = Path.Combine(path, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    private async Task<bool> GitOriginMatchesAsync(
        string moduleDir,
        string expectedRepository,
        CancellationToken cancellationToken)
    {
        var origin = await TryGetOriginUrlAsync(moduleDir, cancellationToken);
        // If git cannot report origin (safe.directory, shallow clone), keep the existing tree.
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        return ModuleCompileEnvironment.SameGitRepository(origin, expectedRepository);
    }

    private static async Task<string?> TryGetOriginUrlAsync(string gitRepoPath, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunGitCaptureAsync(
            ["remote", "get-url", "origin"],
            gitRepoPath,
            cancellationToken,
            TimeSpan.FromSeconds(15));
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }

    private static async Task RunGitOrThrowAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var (exitCode, _, stderr) = await RunGitCaptureAsync(arguments, workingDirectory, cancellationToken, timeout);
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? $"git exited with code {exitCode}" : stderr;
            throw new InvalidOperationException(detail);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitCaptureAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        GitExecutable.ApplyTo(process.StartInfo);
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException($"Timed out running git {string.Join(' ', arguments)}.");
        }

        var stdout = (await outputTask).Trim();
        var stderr = (await errorTask).Trim();
        return (process.ExitCode, stdout, stderr);
    }

    private static void ApplyFileName(ProcessStartInfo startInfo, string fileName)
    {
        if (string.Equals(fileName, "git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "git.exe", StringComparison.OrdinalIgnoreCase))
        {
            GitExecutable.ApplyTo(startInfo);
            return;
        }

        startInfo.FileName = fileName;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort — the process may have exited between the check and the kill.
        }
    }
}

/// <summary>
/// Module version information for tracking
/// </summary>
internal record ModuleVersionInfo
{
    public string ModuleId { get; init; } = string.Empty;
    public string CommitSha { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public string Branch { get; init; } = string.Empty;
}
