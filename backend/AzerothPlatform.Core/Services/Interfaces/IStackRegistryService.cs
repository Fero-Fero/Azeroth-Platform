using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Computes the replicated multi-stack registry (the launcher's server list) from every launcher-visible
/// stack and pushes it as <c>portal.json</c> into each stack's own client container. Stacks then serve
/// the registry from <c>GET /portal</c>, so the launcher keeps multi-stack functionality without the
/// manager being reachable in the player path.
/// </summary>
public interface IStackRegistryService
{
    /// <summary>Builds the current registry snapshot document (without pushing it).</summary>
    Task<StackPortalDocument> BuildDocumentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the registry snapshot and pushes it to every launcher-visible stack that has a client
    /// container. Best-effort per stack: an unreachable stack is logged and skipped so it does not block
    /// the others (it self-heals from the registry once it is reachable again).
    /// </summary>
    Task RebuildAndPushAsync(CancellationToken cancellationToken = default);
}
