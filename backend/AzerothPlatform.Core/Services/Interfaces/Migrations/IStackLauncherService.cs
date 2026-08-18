using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Serves a stack's per-stack client distribution (launcher config, manifest, and files) from
/// <c>{stackRoot}/client</c>, using the stack's realm name and auth port for branding/realmlist.
/// </summary>
public interface IStackLauncherService
{
    /// <summary>
    /// Throws <see cref="KeyNotFoundException"/> when the stack does not exist OR is not marked
    /// <c>LauncherVisible</c>. Used by the anonymous per-stack launcher endpoints so a non-listed stack
    /// is indistinguishable from a missing one (prevents enumerating hidden stacks by GUID).
    /// </summary>
    Task EnsureLauncherVisibleAsync(string stackId, CancellationToken cancellationToken = default);

    Task<LauncherConfigDto> GetConfigAsync(string stackId, CancellationToken cancellationToken = default);

    Task<ClientManifest> GetManifestAsync(string stackId, CancellationToken cancellationToken = default);

    Task<ClientManifest> RescanAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bumps the stack client's verify token and rescans, forcing every launcher pointed at this stack
    /// to full-verify (re-hash) all files on its next check.
    /// </summary>
    Task<ClientManifest> ForceVerifyAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the client-server hash cache, rebuilds the manifest from disk, and bumps the verify token
    /// so every launcher re-syncs all distributable files on its next check.
    /// </summary>
    Task<ClientManifestRebuildResultDto> RebuildManifestAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a manifest-relative path to an absolute file path (traversal-guarded).</summary>
    Task<string?> ResolveFilePathAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the raw <c>WTF/Config.wtf</c> settings template for this stack (with the
    /// <c>{{HOST}}</c>/<c>{{PORT}}</c> placeholders intact) so an admin can edit it. Falls back to the
    /// baked default template when the stack has none yet.
    /// </summary>
    Task<string> GetConfigTemplateAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites this stack's <c>WTF/Config.wtf</c> settings template. The content may use the
    /// <c>{{HOST}}</c>/<c>{{PORT}}</c> placeholders, which are substituted per launcher when served.
    /// </summary>
    Task SaveConfigTemplateAsync(string stackId, string content, CancellationToken cancellationToken = default);
}
