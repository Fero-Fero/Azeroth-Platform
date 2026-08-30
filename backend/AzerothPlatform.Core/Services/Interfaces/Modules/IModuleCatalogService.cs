using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Provides available AzerothCore modules for setup flows. The catalog combines built-in modules
/// (defined in code) with custom modules added through the admin catalog.
/// </summary>
public interface IModuleCatalogService
{
    /// <summary>Lists modules available for setup, optionally filtered by server type.</summary>
    Task<IReadOnlyList<ModuleDto>> ListAsync(ServerType? serverType = null, CancellationToken cancellationToken = default);

    /// <summary>Lists every module (built-in + custom) unfiltered, for catalog administration.</summary>
    Task<IReadOnlyList<ModuleDto>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a new custom module cloned from a git repository.</summary>
    Task<ModuleDto> CreateAsync(SaveModuleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Adds a new custom module from an uploaded package (.zip).</summary>
    Task<ModuleDto> CreateFromPackageAsync(SaveModuleRequest request, string fileName, Stream zipContent, CancellationToken cancellationToken = default);

    /// <summary>Replaces the stored package files of an existing package module.</summary>
    Task<ModuleDto> ReplacePackageAsync(string moduleId, string fileName, Stream zipContent, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing custom module's metadata. Built-in modules cannot be updated.</summary>
    Task<ModuleDto> UpdateAsync(string moduleId, SaveModuleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a custom module. Built-in modules cannot be deleted.</summary>
    Task DeleteAsync(string moduleId, CancellationToken cancellationToken = default);

    /// <summary>Gets a module's README (fetched from git or read from the uploaded package).</summary>
    Task<ModuleReadmeDto> GetReadmeAsync(string moduleId, CancellationToken cancellationToken = default);
}
