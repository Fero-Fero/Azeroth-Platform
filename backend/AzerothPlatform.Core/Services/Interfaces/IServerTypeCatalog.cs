using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Central, configuration-driven registry that maps a <see cref="ServerType"/> to the core repository
/// it is built from and governs which catalog modules are visible for each type (and from which
/// repository they are cloned). Backed by the operator-editable server-type catalog configuration.
/// </summary>
public interface IServerTypeCatalog
{
    /// <summary>Enabled server types with wizard display metadata (config order preserved).</summary>
    IReadOnlyList<ServerTypeInfoDto> GetServerTypes();

    /// <summary>The core repository URL + branch to clone for the given server type.</summary>
    (string RepositoryUrl, string Branch) GetCoreRepository(ServerType serverType);

    /// <summary>The default core branch for the given server type.</summary>
    string GetCoreBranch(ServerType serverType);

    /// <summary>
    /// Whether the given server type expects a user-supplied core repository (custom fork) rather than
    /// a fixed catalog repository.
    /// </summary>
    bool AllowsCustomRepository(ServerType serverType);

    /// <summary>
    /// Whether a module should be shown/selectable for the given server type, taking into account
    /// bundled modules and explicit visibility rules. Modules without a rule are visible for all types.
    /// </summary>
    bool IsModuleVisible(string moduleId, ServerType serverType);

    /// <summary>Module ids that must be included in <see cref="StackConfigurationDto.ModuleIds"/> for this server type.</summary>
    IReadOnlyList<string> GetRequiredModuleIds(ServerType serverType);

    /// <summary>
    /// The repository URL + branch a module should be cloned from for the given server type, applying
    /// any per-type override (falling back to the module's own repository/branch).
    /// </summary>
    (string Repository, string Branch) ResolveModuleRepository(
        string moduleId,
        string defaultRepository,
        string defaultBranch,
        ServerType serverType);

    /// <summary>
    /// Best-effort reverse lookup of a server type from a cloned core repository URL (and branch).
    /// Returns null when no configured type matches.
    /// </summary>
    ServerType? InferServerType(string? repositoryUrl, string? branch);
}
