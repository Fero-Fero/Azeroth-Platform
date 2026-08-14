using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
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

    private static readonly ConcurrentDictionary<string, BuildStatusDto> BuildStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> BuildCancellations = new(StringComparer.OrdinalIgnoreCase);
    
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
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        
        var stack = await dbContext.ManagedStacks.SingleAsync(item => item.Id == stackId, cancellationToken);

        // Cancel any existing build before starting a new one (allows rebuilding stuck/failed builds)
        if (BuildStates.TryGetValue(stackId, out var existingBuild))
        {
            if (existingBuild.CurrentPhase is not (BuildPhase.Completed or BuildPhase.Failed))
            {
                _logger.LogWarning("Cancelling existing build for stack {StackId} to start rebuild", stackId);
                if (BuildCancellations.TryGetValue(stackId, out var existingCts))
                {
                    existingCts.Cancel();
                }
            }
        }

        // If no configuration provided, use existing stack configuration (for rebuilds)
        var buildConfig = configuration ?? new StackConfigurationDto
        {
            StackName = stack.StackName,
            ServerType = stack.ServerType,
            ModuleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson) ?? [],
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
                CustomEnvVars = JsonSerializer.Deserialize<Dictionary<string, string>>(stack.CustomEnvVarsJson) ?? new Dictionary<string, string>()
            }
        };
        
        if (buildConfig is null)
        {
            throw new InvalidOperationException("Configuration is required to start a build (no existing configuration found)");
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
            Services.Migrations.MigrationLayout.EnsureScaffold(buildPath, _migrationOptions.ClientSettingsTemplatePath);

            // Mark all directories as safe for git (avoids "dubious ownership" errors in Docker
            // where files may be owned by a different UID than the running process)
            await RunProcessAsync(stackId, "git", "config --global --add safe.directory *", buildPath, cancellationToken);

            // Determine repository URL and branch
            // For updates (configuration is null), use stored values from database if available
            // For new builds, use the configuration-based defaults
            string repoUrl;
            string branch;
            var configMigrationMode = ConfigMigrationMode.Skip;
            
            using (var scope = _scopeFactory.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
                var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

                postBuildAction = stack?.PostBuildAction ?? PostBuildAction.None;
                configMigrationMode = stack?.ConfigMigrationMode ?? ConfigMigrationMode.Skip;

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

            await CloneRepositoryAsync(stackId, buildPath, repoUrl, branch, configuration, cancellationToken);
            _logger.LogInformation("Repository cloned successfully for stack {StackId}", stackId);
            
            await PrepareModulesAsync(stackId, buildPath, configuration, cancellationToken);
            _logger.LogInformation("Modules prepared for stack {StackId}", stackId);
            
            await GenerateDockerComposeAsync(stackId, buildPath, configuration, cancellationToken);
            _logger.LogInformation("Docker Compose generated for stack {StackId}", stackId);
            
            await BuildDockerImagesAsync(stackId, buildPath, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        await UpdateBuildStatusAsync(stackId, BuildPhase.Cloning, 10, "Cloning AzerothCore repository...", null);

        var repoPath = Path.Combine(buildPath, "azerothcore-wotlk");

        if (Directory.Exists(repoPath))
        {
            // A previously interrupted clone/build (or a `git pull` on a shallow clone) can leave the
            // working tree incomplete — most visibly missing src/cmake/macros, which makes CMake fail
            // with "Unknown CMake command GetScriptModuleList". Only reuse a checkout we can verify is
            // intact; otherwise wipe it and re-clone from scratch.
            if (IsCoreCheckoutValid(repoPath))
            {
                await AddLogAsync(stackId, "Repository already exists, refreshing to latest...");
                var safeBranch = ModuleCatalogService.ValidateGitRef(branch);
                await RunProcessArgsAsync(stackId, "git", new[] { "fetch", "--depth", "1", "origin", safeBranch }, repoPath, cancellationToken);
                await RunProcessArgsAsync(stackId, "git", new[] { "reset", "--hard", "FETCH_HEAD" }, repoPath, cancellationToken);
                await RunProcessArgsAsync(stackId, "git", new[] { "clean", "-ffd" }, repoPath, cancellationToken);
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

        await UpdateBuildStatusAsync(stackId, BuildPhase.Cloning, 25, "Repository cloned successfully", null);
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
    /// <item>BuildKit-only flags — <c>RUN --mount=type=cache/bind</c>, <c>COPY --chmod=</c>,
    /// <c>COPY --link</c> — which are stripped. Safe: the ccache mount is a speed-up, the .git bind mount
    /// only feeds the embedded revision string (AzerothCore degrades gracefully without .git), <c>--chmod</c>
    /// is superseded by the file's git mode bits, and <c>--link</c> only affects layer reuse.</item>
    /// <item>The <c>$DOCKER_USER</c> ARG referenced across FROM boundaries. In stages that <em>have</em>
    /// the <c>acore</c> account (the <c>runtime</c> stage — which runs <c>adduser</c> — and everything
    /// <c>FROM runtime</c>), the ARG is re-declared after FROM so <c>COPY --chown=$DOCKER_USER</c> /
    /// <c>USER $DOCKER_USER</c> resolve. In stages that do <em>not</em> create the account (e.g.
    /// <c>client-data</c>, which is <c>FROM skeleton</c>), those references can't resolve to a real user on
    /// the classic builder ("no such user: acore"), so the <c>--chown</c> is dropped and <c>USER</c> is
    /// removed — the step then runs as root, which is fine for an init container populating a volume that
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
        CancellationToken cancellationToken)
    {
        await UpdateBuildStatusAsync(stackId, BuildPhase.PreparingModules, 30, "Preparing modules...", null);

        if (configuration.ModuleIds.Count == 0)
        {
            await AddLogAsync(stackId, "No modules selected, skipping module preparation");
            return;
        }

        await AddLogAsync(stackId, $"Integrating {configuration.ModuleIds.Count} module(s)...");
        
        var modulesPath = Path.Combine(buildPath, "azerothcore-wotlk", "modules");
        Directory.CreateDirectory(modulesPath);

        using var scope = _scopeFactory.CreateScope();
        var moduleCatalog = scope.ServiceProvider.GetRequiredService<IModuleCatalogService>();
        var packageStorage = scope.ServiceProvider.GetRequiredService<IModulePackageStorage>();
        var allModules = await moduleCatalog.ListAsync(configuration.ServerType, cancellationToken);

        foreach (var moduleId in configuration.ModuleIds)
        {
            var module = allModules.FirstOrDefault(m => m.Id == moduleId);
            if (module == null)
            {
                await AddLogAsync(stackId, $"Warning: Module {moduleId} not found in catalog, skipping");
                continue;
            }

            var moduleDir = Path.Combine(modulesPath, moduleId);

            if (module.SourceType == ModuleSource.Package)
            {
                // Uploaded package: copy the stored source tree into the build (replacing any prior copy).
                await AddLogAsync(stackId, $"Copying uploaded package module: {module.Name}");
                if (Directory.Exists(moduleDir))
                {
                    Directory.Delete(moduleDir, recursive: true);
                }
                await packageStorage.CopyToAsync(moduleId, moduleDir, cancellationToken);
            }
            else if (Directory.Exists(moduleDir))
            {
                await AddLogAsync(stackId, $"Module {module.Name} already exists, pulling latest...");
                await RunProcessArgsAsync(stackId, "git", new[] { "pull", "--ff-only" }, moduleDir, cancellationToken);
            }
            else
            {
                await AddLogAsync(stackId, $"Cloning module: {module.Name}");
                var safeBranch = ModuleCatalogService.ValidateGitRef(module.Branch);
                var safeRepo = ModuleCatalogService.ValidateGitRepository(module.Repository);
                await RunProcessArgsAsync(
                    stackId,
                    "git",
                    new[] { "clone", "--depth", "1", "--branch", safeBranch, "--", safeRepo, moduleId },
                    modulesPath,
                    cancellationToken);
            }
        }

        await UpdateBuildStatusAsync(stackId, BuildPhase.PreparingModules, 40, "Modules prepared", null);
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
        var luaDir = Services.Migrations.MigrationLayout.LuaScriptsDir(buildPath);
        var overrideContent = GenerateDockerComposeOverride(stackId, configuration, Directory.Exists(luaDir));
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

        // Quote password to handle special characters
        sb.AppendLine($"DOCKER_DB_ROOT_PASSWORD=\"{config.Database.RootPassword}\"");
        sb.AppendLine($"DOCKER_DB_EXTERNAL_PORT={dataBind}{config.Database.Port}");
        sb.AppendLine($"DOCKER_WORLD_EXTERNAL_PORT={config.Ports.WorldServer}");
        sb.AppendLine($"DOCKER_SOAP_EXTERNAL_PORT={dataBind}{config.Ports.SoapPort}");
        
        // Auth server port - need to override in docker-compose.override.yml
        sb.AppendLine($"DOCKER_AUTH_EXTERNAL_PORT={config.Ports.AuthServer}");
        
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

        return sb.ToString();
    }

    private string GenerateDockerComposeOverride(string stackId, StackConfigurationDto config, bool includeLua)
    {
        var serviceEnvironment = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (serviceId, bucket) in config.Advanced.ServiceEnvVars ?? new())
        {
            if (!string.IsNullOrWhiteSpace(serviceId))
            {
                serviceEnvironment[serviceId] = bucket ?? new Dictionary<string, string>();
            }
        }

        // Fold legacy flat vars into the worldserver bucket when the caller only sent flat vars.
        var legacy = config.Advanced.CustomEnvVars ?? new Dictionary<string, string>();
        if (legacy.Count > 0
            && (!serviceEnvironment.TryGetValue(ServiceEnvTemplateService.Worldserver, out var world) || world.Count == 0))
        {
            serviceEnvironment[ServiceEnvTemplateService.Worldserver] = legacy;
        }

        return DockerComposeOverrideGenerator.Generate(stackId, config.StackName, serviceEnvironment, includeLua);
    }

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
            var composeArgs = string.IsNullOrEmpty(argPrefix) ? "build" : $"{argPrefix} build";

            // Build the default (non-profiled) services: db-import, worldserver, authserver, and
            // client-data. `ac-client-data-init` (target client-data) IS required — it populates the
            // stack's `_ac-client-data` volume that the manager's dbc/maps migration pipeline reads (see
            // MigrationService.Apply) and that worldserver mounts. `ac-tools` / `ac-dev-server` carry
            // compose `profiles:` so a bare `docker compose build` skips them automatically.
            // Use cache for faster builds (removed --no-cache)
            await RunProcessAsync(
                stackId,
                command,
                composeArgs,
                repoPath, // Run from the repo directory where docker-compose.yml is
                cancellationToken);

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

        await AddLogAsync(stackId, "All Docker images are ready");
    }

    private async Task<bool> VerifyImagesExistAsync(string stackId, string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            // Check if the main AzerothCore images exist
            // Try podman first (Fedora), fallback to docker
            var dockerCommand = File.Exists("/usr/bin/podman") ? "podman" : "docker";
            
            // Images are tagged with stackId for isolation. client-data is an init image (populates the
            // shared data volume worldserver reads), so it must build too — verify all four.
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
                        var modulePath = Path.Combine(modulesPath, moduleId);
                        if (Directory.Exists(modulePath))
                        {
                            var module = allModules.FirstOrDefault(m => m.Id == moduleId);
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
    }

    /// <summary>
    /// Streams the locally-built stack images plus the shared armory + client-server images to the
    /// remote engine (best-effort, logged), so a subsequent remote <c>compose up</c> finds every image
    /// it needs and the external stack serves its own armory + client files.
    /// </summary>
    private async Task ShipImagesToRemoteAsync(ManagedStackEntity stack)
    {
        await AddLogAsync(stack.Id, "Shipping built images to remote engine...");
        await _stackImageShipping.ShipStackImagesAsync(stack, includeArmory: true, includeClient: true, CancellationToken.None);
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
                ? "Update failed — rolled back"
                : "Build failed";
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
                stackId, BuildPhase.Failed, 0, "Update failed — rolling back to pre-update snapshot...", null);
            await AddLogAsync(stackId, $"Restoring pre-update snapshot {revisionId}...");

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            var revisions = scope.ServiceProvider.GetRequiredService<IRevisionService>();

            await revisions.RestoreAsync(stackId, revisionId, CancellationToken.None);

            var revision = await dbContext.StackRevisions
                .SingleOrDefaultAsync(r => r.Id == revisionId && r.StackId == stackId);
            var stack = await dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId);
            if (revision is not null && stack is not null)
            {
                stack.CoreCommitSha = revision.CoreCommitSha;
                stack.ModuleVersionsJson = revision.ModuleVersionsJson;
                stack.AppliedPatchLevel = revision.AppliedPatchLevel;
                stack.AppliedPatchesJson = revision.AppliedPatchesJson;
                await dbContext.SaveChangesAsync(CancellationToken.None);
            }

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
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
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
            if (persisted is null
                || persisted.CurrentPhase is BuildPhase.Completed or BuildPhase.Failed)
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
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = gitRepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to get commit SHA: git exited with code {process.ExitCode}");
        }

        return output.Trim();
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
