using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Configuration-driven implementation of <see cref="IServerTypeCatalog"/>. Reads the operator-editable
/// <c>ServerTypeCatalog</c> section (falling back to <see cref="ServerTypeCatalogOptions.Defaults"/> when
/// a section is absent) and answers repository/branch and module-visibility questions for a server type.
/// </summary>
public sealed class ServerTypeCatalog : IServerTypeCatalog
{
    private readonly IReadOnlyList<ServerTypeDefinition> _serverTypes;
    private readonly IReadOnlyDictionary<string, ModuleVisibilityRule> _moduleRules;

    public ServerTypeCatalog(IOptions<ServerTypeCatalogOptions> options)
    {
        var configured = options.Value;
        var defaults = ServerTypeCatalogOptions.Defaults;

        // Evaluate the two sections independently so an operator can override only one of them in
        // configuration without silently losing the built-in defaults for the other.
        _serverTypes = configured.ServerTypes.Count > 0 ? configured.ServerTypes : defaults.ServerTypes;
        var rules = configured.ModuleRules.Count > 0 ? configured.ModuleRules : defaults.ModuleRules;

        _moduleRules = rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.ModuleId))
            .GroupBy(rule => rule.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ServerTypeInfoDto> GetServerTypes() =>
        _serverTypes
            .Where(definition => definition.Enabled)
            .Select(definition => new ServerTypeInfoDto
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                Description = definition.Description,
                Icon = definition.Icon,
                CoreRepositoryUrl = definition.CoreRepositoryUrl,
                CoreBranch = definition.CoreBranch,
                AllowCustomRepository = definition.AllowCustomRepository,
                RequiredModuleIds = definition.RequiredModuleIds.ToList()
            })
            .ToList();

    public IReadOnlyList<string> GetRequiredModuleIds(ServerType serverType) =>
        Find(serverType)?.RequiredModuleIds ?? [];

    public (string RepositoryUrl, string Branch) GetCoreRepository(ServerType serverType)
    {
        var definition = Find(serverType);
        if (definition is not null && !string.IsNullOrWhiteSpace(definition.CoreRepositoryUrl))
        {
            return (definition.CoreRepositoryUrl, string.IsNullOrWhiteSpace(definition.CoreBranch) ? "master" : definition.CoreBranch);
        }

        // Safe fallback: the official standard repository on master.
        return ("https://github.com/azerothcore/azerothcore-wotlk.git", "master");
    }

    public string GetCoreBranch(ServerType serverType) => GetCoreRepository(serverType).Branch;

    public bool AllowsCustomRepository(ServerType serverType) => Find(serverType)?.AllowCustomRepository ?? false;

    public bool IsModuleVisible(string moduleId, ServerType serverType)
    {
        var definition = Find(serverType);

        // A module compiled into the core fork must not be offered as an installable module.
        if (definition is not null &&
            definition.BundledModuleIds.Any(id => string.Equals(id, moduleId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_moduleRules.TryGetValue(moduleId, out var rule))
        {
            if (rule.VisibleForServerTypes is { Count: > 0 } allow && !allow.Contains(serverType))
            {
                return false;
            }

            if (rule.HiddenForServerTypes is { Count: > 0 } deny && deny.Contains(serverType))
            {
                return false;
            }
        }

        // No explicit rule (or rule allows it): the module is visible for this server type.
        return true;
    }

    public (string Repository, string Branch) ResolveModuleRepository(
        string moduleId,
        string defaultRepository,
        string defaultBranch,
        ServerType serverType)
    {
        if (_moduleRules.TryGetValue(moduleId, out var rule))
        {
            var over = rule.RepositoryOverrides.FirstOrDefault(o => o.ServerType == serverType);
            if (over is not null && !string.IsNullOrWhiteSpace(over.Repository))
            {
                return (over.Repository, string.IsNullOrWhiteSpace(over.Branch) ? "master" : over.Branch);
            }
        }

        return (defaultRepository, defaultBranch);
    }

    public ServerType? InferServerType(string? repositoryUrl, string? branch)
    {
        var target = NormalizeRepoPath(repositoryUrl);
        if (target is null)
        {
            return null;
        }

        // Prefer a match that also agrees on branch (distinguishes forks that share a repo path but use
        // different long-lived branches); otherwise accept the first repo-path match.
        ServerType? repoMatch = null;
        foreach (var definition in _serverTypes)
        {
            if (NormalizeRepoPath(definition.CoreRepositoryUrl) != target)
            {
                continue;
            }

            repoMatch ??= definition.Id;

            if (!string.IsNullOrWhiteSpace(branch) &&
                branch.Equals(definition.CoreBranch, StringComparison.OrdinalIgnoreCase))
            {
                return definition.Id;
            }
        }

        return repoMatch;
    }

    private ServerTypeDefinition? Find(ServerType serverType) =>
        _serverTypes.FirstOrDefault(definition => definition.Id == serverType);

    /// <summary>Reduces a git URL to a normalized "owner/repo" for comparison (host/scheme/.git agnostic).</summary>
    private static string? NormalizeRepoPath(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return null;
        }

        var url = repositoryUrl.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
        }

        var host = url.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (host >= 0)
        {
            // Skip past "github.com" and any following ':' (ssh) or '/' (https).
            var rest = url[(host + "github.com".Length)..].TrimStart(':', '/');
            return rest.ToLowerInvariant();
        }

        return url.ToLowerInvariant();
    }
}
