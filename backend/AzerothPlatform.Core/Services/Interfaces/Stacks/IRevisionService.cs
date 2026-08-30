using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Creates, lists, restores, and deletes point-in-time snapshots ("revisions") of a stack's
/// databases, server configuration, and (for pre-update checkpoints) Docker images, so an
/// update that breaks something can be rolled back.
/// </summary>
public interface IRevisionService
{
    /// <summary>Lists a stack's revisions, newest first.</summary>
    Task<IReadOnlyList<RevisionDto>> ListAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a snapshot: ensures the database container is up, dumps acore_world/auth/characters,
    /// copies the .conf files, and records metadata. <paramref name="reason"/> is "manual" or
    /// "pre-update".
    /// </summary>
    Task<RevisionDto> CreateAsync(string stackId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tags the stack's current Docker images as this revision's checkpoint so a later rebuild of
    /// <c>:{stackId}</c> does not lose the previous binaries. Untags checkpoint images from older
    /// pre-update revisions (SQL dumps for those revisions stay).
    /// </summary>
    Task PreserveCheckpointImagesAsync(string stackId, string revisionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a revision: stops world/auth if they are up, drops and recreates the three
    /// AzerothCore databases from the dump, restores snapshotted .conf files, retags checkpoint
    /// images when present, writes version metadata back, and refreshes update-check flags.
    /// </summary>
    Task RestoreAsync(string stackId, string revisionId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a revision and its on-disk dump files.</summary>
    Task DeleteAsync(string stackId, string revisionId, CancellationToken cancellationToken = default);
}
