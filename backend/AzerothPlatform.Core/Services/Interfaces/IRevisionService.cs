using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Creates, lists, restores, and deletes point-in-time snapshots ("revisions") of a stack's
/// databases and server configuration, so an update that breaks something can be rolled back.
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
    /// Restores a revision: drops and recreates the three AzerothCore databases from the dump and
    /// restores the snapshotted .conf files. The caller should restart the stack afterwards.
    /// </summary>
    Task RestoreAsync(string stackId, string revisionId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a revision and its on-disk dump files.</summary>
    Task DeleteAsync(string stackId, string revisionId, CancellationToken cancellationToken = default);
}
