using System.Diagnostics;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Service for discovering existing AzerothCore stacks from filesystem and Docker
/// </summary>
public class StackDiscoveryService : IStackDiscoveryService
{
    private readonly IDockerClient _dockerClient;
    private readonly ILogger<StackDiscoveryService> _logger;
    private readonly IServerTypeCatalog _serverTypeCatalog;
    private readonly string _stacksPath;

    public StackDiscoveryService(
        ILogger<StackDiscoveryService> logger,
        IServerTypeCatalog serverTypeCatalog,
        IOptions<DockerOptions> dockerOptions)
    {
        _logger = logger;
        _serverTypeCatalog = serverTypeCatalog;
        _stacksPath = dockerOptions.Value.BuildsPath;
        
        // Create Docker client like DockerService does
        var config = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"));
        _dockerClient = config.CreateClient();
    }

    public async Task<IReadOnlyList<DiscoveredStackDto>> DiscoverStacksAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting stack discovery scan in {StacksPath}", _stacksPath);
        
        if (!Directory.Exists(_stacksPath))
        {
            _logger.LogWarning("Stacks directory does not exist: {StacksPath}", _stacksPath);
            return Array.Empty<DiscoveredStackDto>();
        }

        var stackDirectories = Directory.GetDirectories(_stacksPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();

        _logger.LogInformation("Found {Count} potential stack directories", stackDirectories.Count);

        var discovered = new List<DiscoveredStackDto>();

        foreach (var stackId in stackDirectories)
        {
            try
            {
                var stack = await DiscoverStackByIdAsync(stackId!, ct);
                if (stack != null)
                {
                    discovered.Add(stack);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error discovering stack {StackId}", stackId);
            }
        }

        _logger.LogInformation("Discovered {Count} importable stacks", discovered.Count);
        return discovered;
    }

    public async Task<DiscoveredStackDto?> DiscoverStackByIdAsync(string stackId, CancellationToken ct = default)
    {
        _logger.LogDebug("Discovering stack {StackId}", stackId);

        var stackPath = Path.Combine(_stacksPath, stackId);
        if (!Directory.Exists(stackPath))
        {
            _logger.LogWarning("Stack directory not found: {StackPath}", stackPath);
            return null;
        }

        // Check if Docker containers exist for this stack
        var projectName = $"acore-{stackId}";
        var containers = await GetContainersForProjectAsync(projectName, ct);

        if (containers.Count == 0)
        {
            _logger.LogWarning("No containers found for stack {StackId} (project: {ProjectName})", stackId, projectName);
            return new DiscoveredStackDto
            {
                StackId = stackId,
                SuggestedName = $"Imported Stack {stackId.Substring(0, Math.Min(8, stackId.Length))}",
                IsOrphaned = true,
                DiscoveredAt = DateTime.UtcNow
            };
        }

        // Extract configuration from containers
        var ports = await ExtractPortMappingsAsync(containers, ct);
        var status = CalculateStackStatus(containers);
        
        // Query git repository for version info
        var gitInfo = await QueryGitRepositoryAsync(stackPath, ct);
        
        // Discover additional data from filesystem
        var coreRepoPath = Path.Combine(stackPath, "azerothcore-wotlk");
        var modules = DiscoverModules(coreRepoPath);
        var envData = ReadEnvFile(coreRepoPath);
        var discoveredEnvVars = ReadDockerComposeOverride(coreRepoPath);
        
        _logger.LogDebug("Stack {StackId}: Modules={ModuleCount}, HasDbPassword={HasPassword}, HasSoapUsername={HasSoapUser}",
            stackId, modules?.Count ?? 0, !string.IsNullOrEmpty(envData.DatabasePassword), !string.IsNullOrEmpty(envData.SoapUsername));

        var discovered = new DiscoveredStackDto
        {
            StackId = stackId,
            SuggestedName = $"Imported Stack {stackId.Substring(0, Math.Min(8, stackId.Length))}",
            InferredServerType = InferServerType(gitInfo.RepositoryUrl, gitInfo.Branch),
            CurrentStatus = status,
            DatabasePort = ports.DatabasePort,
            AuthServerPort = ports.AuthServerPort,
            WorldServerPort = ports.WorldServerPort,
            SoapPort = ports.SoapPort,
            IsOrphaned = false,
            ContainerNames = containers.Select(c => c.Names.FirstOrDefault()?.TrimStart('/') ?? c.ID).ToList(),
            CoreRepositoryUrl = gitInfo.RepositoryUrl,
            CoreBranch = gitInfo.Branch,
            CoreCommitSha = gitInfo.CommitSha,
            DiscoveredAt = DateTime.UtcNow,
            DiscoveredModules = modules,
            DiscoveredDatabasePassword = envData.DatabasePassword,
            DiscoveredSoapUsername = envData.SoapUsername,
            DiscoveredSoapPassword = envData.SoapPassword,
            DiscoveredEnvVars = discoveredEnvVars
        };

        _logger.LogInformation(
            "Discovered stack {StackId}: Type={Type}, Status={Status}, Containers={ContainerCount}",
            stackId, discovered.InferredServerType, discovered.CurrentStatus, discovered.ContainerNames.Count);

        return discovered;
    }

    private async Task<List<ContainerListResponse>> GetContainersForProjectAsync(string projectName, CancellationToken ct)
    {
        var parameters = new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool>
                {
                    [$"com.docker.compose.project={projectName}"] = true
                }
            }
        };

        var containers = await _dockerClient.Containers.ListContainersAsync(parameters, ct);
        return containers.ToList();
    }

    private async Task<(int DatabasePort, int AuthServerPort, int WorldServerPort, int SoapPort)> ExtractPortMappingsAsync(
        List<ContainerListResponse> containers, CancellationToken ct)
    {
        int dbPort = 0, authPort = 0, worldPort = 0, soapPort = 0;

        foreach (var container in containers)
        {
            var serviceName = container.Labels.TryGetValue("com.docker.compose.service", out var service) 
                ? service 
                : "";

            // Database container
            if (serviceName == "ac-database")
            {
                dbPort = GetHostPort(container.Ports, 3306);
            }
            // Auth server container
            else if (serviceName == "ac-authserver")
            {
                authPort = GetHostPort(container.Ports, 3724);
            }
            // World server container
            else if (serviceName == "ac-worldserver")
            {
                worldPort = GetHostPort(container.Ports, 8085);
                if (soapPort == 0)
                {
                    soapPort = GetHostPort(container.Ports, 7878);
                }
            }
        }

        _logger.LogDebug(
            "Extracted ports: DB={DatabasePort}, Auth={AuthPort}, World={WorldPort}, SOAP={SoapPort}",
            dbPort, authPort, worldPort, soapPort);

        return (dbPort, authPort, worldPort, soapPort);
    }

    private static int GetHostPort(IList<Port> ports, int containerPort)
    {
        var port = ports.FirstOrDefault(p => p.PrivatePort == containerPort && p.PublicPort > 0);
        return (int)(port?.PublicPort ?? 0);
    }

    private static StackStatus CalculateStackStatus(List<ContainerListResponse> containers)
    {
        // Critical containers for a functional stack
        var criticalServices = new[] { "ac-database", "ac-authserver", "ac-worldserver" };
        
        var criticalContainers = containers
            .Where(c => {
                var hasService = c.Labels.TryGetValue("com.docker.compose.service", out var service);
                return hasService && criticalServices.Contains(service);
            })
            .ToList();

        if (criticalContainers.Count == 0)
        {
            return StackStatus.Stopped;
        }

        var runningCount = criticalContainers.Count(c => c.State == "running");
        var totalCritical = criticalContainers.Count;

        if (runningCount == 0)
        {
            return StackStatus.Stopped;
        }
        else if (runningCount < totalCritical)
        {
            return StackStatus.Degraded;
        }
        else
        {
            // All critical containers running, check health
            var unhealthyCount = criticalContainers.Count(c => 
                c.Status.Contains("unhealthy", StringComparison.OrdinalIgnoreCase));
            
            return unhealthyCount > 0 ? StackStatus.Degraded : StackStatus.Running;
        }
    }

    private async Task<(string? RepositoryUrl, string? Branch, string? CommitSha)> QueryGitRepositoryAsync(
        string stackPath, CancellationToken ct)
    {
        var repoPath = Path.Combine(stackPath, "azerothcore-wotlk");
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        {
            _logger.LogWarning("Git repository not found at {RepoPath}", repoPath);
            return (null, null, null);
        }

        try
        {
            var remoteUrl = await RunGitCommandAsync(repoPath, "remote get-url origin", ct);
            var branch = await RunGitCommandAsync(repoPath, "rev-parse --abbrev-ref HEAD", ct);
            var commitSha = await RunGitCommandAsync(repoPath, "rev-parse HEAD", ct);

            _logger.LogDebug(
                "Git info: Remote={RemoteUrl}, Branch={Branch}, Commit={CommitSha}",
                remoteUrl, branch, commitSha);

            return (remoteUrl, branch, commitSha);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying git repository at {RepoPath}", repoPath);
            return (null, null, null);
        }
    }

    private async Task<string?> RunGitCommandAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start git process");
        }

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            _logger.LogWarning("Git command failed: {Arguments}, Error: {Error}", arguments, error);
            return null;
        }

        return output.Trim();
    }

    private ServerType InferServerType(string? repositoryUrl, string? branch)
    {
        if (string.IsNullOrEmpty(repositoryUrl))
        {
            _logger.LogWarning("Repository URL is empty, defaulting to Standard server type");
            return ServerType.Standard;
        }

        // Match the cloned core repository against the operator-configured server-type catalog, so newly
        // added variants (Individual Progression, NPCBots, ...) are recognized without touching this code.
        var inferred = _serverTypeCatalog.InferServerType(repositoryUrl, branch);
        if (inferred is not null)
        {
            _logger.LogInformation(
                "Detected {ServerType} from repository: URL={Url}, Branch={Branch}",
                inferred.Value, repositoryUrl, branch);
            return inferred.Value;
        }

        // Unknown repository - log warning and default to Standard
        _logger.LogWarning(
            "Unknown repository URL pattern: {Url}. Defaulting to Standard server type. " +
            "Stack may not update correctly.", 
            repositoryUrl);
        
        return ServerType.Standard;
    }
    
    /// <summary>
    /// Discovers installed modules by scanning the modules directory.
    /// </summary>
    private List<string>? DiscoverModules(string coreRepoPath)
    {
        var modulesPath = Path.Combine(coreRepoPath, "modules");
        if (!Directory.Exists(modulesPath))
        {
            _logger.LogDebug("Modules directory not found at {ModulesPath}", modulesPath);
            return null;
        }

        try
        {
            var moduleDirectories = Directory.GetDirectories(modulesPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name) && name!.StartsWith("mod-", StringComparison.OrdinalIgnoreCase))
                .Cast<string>()
                .ToList();

            if (moduleDirectories.Count > 0)
            {
                _logger.LogDebug("Discovered {ModuleCount} modules: {Modules}", 
                    moduleDirectories.Count, string.Join(", ", moduleDirectories));
                return moduleDirectories;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover modules from {ModulesPath}", modulesPath);
        }

        return null;
    }
    
    /// <summary>
    /// Reads the .env file to recover passwords and configuration.
    /// </summary>
    private (string? DatabasePassword, string? SoapUsername, string? SoapPassword) ReadEnvFile(string coreRepoPath)
    {
        var envPath = Path.Combine(coreRepoPath, ".env");
        if (!File.Exists(envPath))
        {
            _logger.LogDebug(".env file not found at {EnvPath}", envPath);
            return (null, null, null);
        }

        try
        {
            var envLines = File.ReadAllLines(envPath);
            string? dbPassword = null;
            string? soapUsername = null;
            string? soapPassword = null;

            foreach (var line in envLines)
            {
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                    continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();
                
                // Remove surrounding quotes if present
                if (value.StartsWith('"') && value.EndsWith('"'))
                {
                    value = value.Substring(1, value.Length - 2);
                }
                
                // Note: In .env files, $$ is used to represent a literal $.
                // We keep $$ as-is since that's what Docker Compose expects.
                // No unescaping needed here - pass through verbatim.

                switch (key)
                {
                    case "DOCKER_DB_ROOT_PASSWORD":
                        dbPassword = value;
                        break;
                    case "SOAP_USERNAME":
                        soapUsername = value;
                        break;
                    case "SOAP_PASSWORD":
                        soapPassword = value;
                        break;
                }
            }

            if (!string.IsNullOrEmpty(dbPassword))
            {
                _logger.LogDebug("Recovered database password from .env file");
            }

            return (dbPassword, soapUsername, soapPassword);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read .env file from {EnvPath}", envPath);
            return (null, null, null);
        }
    }
    
    /// <summary>
    /// Reads custom environment variables from docker-compose.override.yml.
    /// Note: This is a simplified parser for common patterns.
    /// </summary>
    private Dictionary<string, string>? ReadDockerComposeOverride(string coreRepoPath)
    {
        var overridePath = Path.Combine(coreRepoPath, "docker-compose.override.yml");
        if (!File.Exists(overridePath))
        {
            _logger.LogDebug("docker-compose.override.yml not found at {OverridePath}", overridePath);
            return null;
        }

        try
        {
            // For now, we'll just note that the file exists
            // A full YAML parser would be needed for complete env var extraction
            // This is a placeholder for future enhancement
            _logger.LogDebug("docker-compose.override.yml found (env var parsing not yet implemented)");
            return new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read docker-compose.override.yml from {OverridePath}", overridePath);
            return null;
        }
    }
}
