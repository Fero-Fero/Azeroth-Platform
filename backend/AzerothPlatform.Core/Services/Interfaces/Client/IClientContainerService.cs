using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Controls a stack's self-contained client-server container (<c>azeroth-platform-client</c>): triggers
/// its authenticated manifest rescan / force-verify from inside the container, context-aware for
/// external stacks. Kept separate from file distribution because the container now owns the manifest.
/// </summary>
public interface IClientContainerService
{
    /// <summary>
    /// Triggers <c>POST /rescan</c> on the stack's client container so its manifest version bumps after
    /// client content changes. No-op (returns false) when the stack has no client container.
    /// </summary>
    Task<bool> RescanAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers <c>POST /force-verify</c> on the stack's client container so its verify token rotates,
    /// forcing launchers to re-hash every file on their next check. No-op when there is no container.
    /// </summary>
    Task<bool> ForceVerifyAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads <c>GET /manifest-status</c> so the manager can report the manifest launchers are actually
    /// being served. Returns null when the stack has no client container, or when the container cannot
    /// be reached — an unreachable container is a normal state (stopped stack), not an error.
    ///
    /// Each read costs a <c>docker exec</c>, so results are cached briefly. Pass
    /// <paramref name="refresh"/> after changing client content, where the point of reading is to see
    /// the change.
    /// </summary>
    Task<ClientManifestStatus?> GetManifestStatusAsync(
        string stackId, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the replicated registry snapshot (<c>portal.json</c>) into the stack's client container
    /// cache volume by execing into the container (context-aware for external stacks, no manager-to-port
    /// networking needed). The container serves it at <c>GET /portal</c>. No-op (returns false) when the
    /// stack has no client container.
    /// </summary>
    Task<bool> PushPortalAsync(string stackId, string portalJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes (or clears) a stack's launcher branding image into its client container cache volume by
    /// execing into the container. The container serves it at <c>GET /branding/{background|logo}</c>.
    /// Passing null <paramref name="content"/> removes the file. The <paramref name="assetName"/> must be
    /// <c>background</c> or <c>logo</c>. No-op (returns false) when the stack has no client container.
    /// </summary>
    Task<bool> PushBrandingAsync(string stackId, string assetName, byte[]? content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes (or clears) a stack's launcher news feed into its client container cache volume by execing
    /// into the container. The container serves the feed at <c>GET /news</c> and each cover image at
    /// <c>GET /news-image/{id}</c>. Passing null/empty <paramref name="newsJson"/> removes the feed and all
    /// covers. <paramref name="coverImages"/> maps a news item id to its cover image bytes. No-op (returns
    /// false) when the stack has no client container.
    /// </summary>
    Task<bool> PushNewsAsync(
        string stackId,
        string? newsJson,
        IReadOnlyDictionary<string, byte[]> coverImages,
        CancellationToken cancellationToken = default);
}
