namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Builds a per-stack armory (frontend-armory) Docker image on the host daemon so each stack can
/// reference its own image by tag. The per-stack image bakes in that stack's uploaded static web
/// assets (and small server-side data), so static changes are stack-scoped.
/// </summary>
public interface IArmoryImageService
{
    /// <summary>The Docker image tag used for a stack's armory (bakes that stack's static assets).</summary>
    string ImageNameFor(string stackId);

    /// <summary>
    /// Builds the stack's armory image if it does not already exist. Safe to call repeatedly;
    /// concurrent callers share a single build.
    /// </summary>
    Task EnsureImageAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the stack's armory image from the current source + uploaded static/data unconditionally,
    /// even if it already exists. Used by "Rebuild &amp; Restart" and after a static-assets upload so
    /// changes are actually picked up (a plain <see cref="EnsureImageAsync"/> short-circuits on a cached image).
    /// </summary>
    Task RebuildImageAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies saved layout JSON, generated placement CSS, and layout-aware templates into a running
    /// armory container so layout edits take effect without a full image rebuild.
    /// </summary>
    Task SyncLiveLayoutAsync(string stackId, CancellationToken cancellationToken = default);
}
