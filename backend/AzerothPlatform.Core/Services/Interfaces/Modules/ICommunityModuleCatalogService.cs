using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Browse and import modules from the AzerothCore community catalogue (GitHub topic metadata).
/// </summary>
public interface ICommunityModuleCatalogService
{
    Task<CommunityModuleListResult> ListAsync(
        string? search = null,
        string? sort = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a community module exists in the platform catalog and returns the catalog entry.
    /// Built-in modules are returned as-is; custom entries are created when missing.
    /// </summary>
    Task<ModuleDto> ImportAsync(string repository, CancellationToken cancellationToken = default);
}
