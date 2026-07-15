using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Serves the distributable WoW client to the launcher: manifest generation,
/// launcher configuration, and file resolution.
/// </summary>
public interface IClientDistributionService
{
    /// <summary>
    /// Returns the current client manifest, generating (and caching) it if necessary.
    /// </summary>
    Task<ClientManifest> GetManifestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns launcher configuration including rendered settings files.
    /// </summary>
    Task<LauncherConfigDto> GetLauncherConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a manifest-relative path to an absolute file path on disk, guarding against
    /// path traversal. Returns null when the file does not exist or the path is invalid.
    /// </summary>
    string? ResolveFilePath(string relativePath);

    /// <summary>
    /// Forces a full rescan and rebuilds the cached manifest.
    /// </summary>
    Task<ClientManifest> RescanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Bumps the manifest's verify token and rescans, so launchers full-verify (re-hash) every file on
    /// their next check even when the content version is unchanged.
    /// </summary>
    Task<ClientManifest> ForceVerifyAsync(CancellationToken cancellationToken = default);

    // ===== Context-scoped variants (used for per-stack client roots) =====

    /// <summary>Returns the manifest for a specific client root, generating and caching it.</summary>
    Task<ClientManifest> GetManifestAsync(ClientDistributionContext context, CancellationToken cancellationToken = default);

    /// <summary>Returns launcher configuration for a specific client root.</summary>
    Task<LauncherConfigDto> GetLauncherConfigAsync(ClientDistributionContext context, CancellationToken cancellationToken = default);

    /// <summary>Resolves a manifest-relative path within a specific client root (traversal-guarded).</summary>
    string? ResolveFilePath(ClientDistributionContext context, string relativePath);

    /// <summary>Forces a rescan of a specific client root and rebuilds its cached manifest.</summary>
    Task<ClientManifest> RescanAsync(ClientDistributionContext context, CancellationToken cancellationToken = default);

    /// <summary>Bumps the verify token for a specific client root and rescans it.</summary>
    Task<ClientManifest> ForceVerifyAsync(ClientDistributionContext context, CancellationToken cancellationToken = default);
}
