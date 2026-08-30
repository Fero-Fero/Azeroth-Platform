using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Serves the distributable WoW client to the launcher: manifest generation,
/// launcher configuration, and file resolution for a per-stack client root.
/// </summary>
public interface IClientDistributionService
{
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
