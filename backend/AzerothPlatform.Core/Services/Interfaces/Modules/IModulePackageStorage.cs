namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Stores uploaded module packages (extracted source trees) so they can be copied into a build,
/// instead of cloning them from git.
/// </summary>
public interface IModulePackageStorage
{
    /// <summary>Whether a stored package exists for the module.</summary>
    bool HasPackage(string moduleId);

    /// <summary>Extracts an uploaded .zip into the module's package directory (replacing any existing files).</summary>
    Task<int> SavePackageAsync(string moduleId, Stream zipContent, CancellationToken cancellationToken = default);

    /// <summary>Deletes the module's stored package directory (no-op if absent).</summary>
    void DeletePackage(string moduleId);

    /// <summary>Copies the stored package into <paramref name="destinationDir"/> (used at build time).</summary>
    Task CopyToAsync(string moduleId, string destinationDir, CancellationToken cancellationToken = default);

    /// <summary>Returns the module's README markdown, or null when none is present.</summary>
    Task<string?> ReadReadmeAsync(string moduleId, CancellationToken cancellationToken = default);
}
