using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Manages per-stack migration/patch folders (SQL, DBC, map, MPQ), file CRUD, baseline capture,
/// and incremental patch application.
/// </summary>
public interface IMigrationService
{
    /// <summary>Lists all patches for a stack with their status and file counts.</summary>
    Task<MigrationOverviewDto> GetOverviewAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Returns a zip with an empty example patch folder structure and a starter description.md.</summary>
    Task<byte[]> GetPatchTemplateArchiveAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Returns detailed file listing for a single patch.</summary>
    Task<PatchDetailsDto> GetPatchAsync(string stackId, string patchKey, CancellationToken cancellationToken = default);

    /// <summary>Saves description.md / description.txt for a patch.</summary>
    Task<PatchDetailsDto> SavePatchDescriptionAsync(string stackId, string patchKey, string content, CancellationToken cancellationToken = default);

    /// <summary>Creates a new patch folder ("{level}_{name}") with the standard sub-folders.</summary>
    Task<PatchSummaryDto> CreatePatchAsync(string stackId, CreatePatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a patch collection archive. Override preserves indices from the archive; append assigns
    /// the next patch index (1.x, 2.x, 3.x) per expansion. Patch folders must be named
    /// <c>patch {index}</c> or <c>patch {index} {name}</c>.
    /// </summary>
    Task<ImportPatchCollectionResultDto> ImportPatchCollectionAsync(string stackId, Stream zipContent, string mode, CancellationToken cancellationToken = default);

    Task<MergePatchImportResultDto> MergePatchImportAsync(
        string stackId,
        string targetPatchKey,
        Stream? sqlArchive,
        Stream? clientArchive,
        CancellationToken cancellationToken = default);

    /// <summary>Lists one directory level of the stack's patch folder tree.</summary>
    Task<ClientBrowseResultDto> BrowsePatchFilesAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Deletes an unapplied patch file or folder from the stack's patch folder tree.</summary>
    Task DeletePatchEntryAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Captures the current DBC set from the running stack's data volume into server_dbc/.</summary>
    Task InitializeBaselineAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches a progress sink that receives live log lines and stage transitions from the next
    /// <see cref="ApplyPatchAsync"/>/<see cref="ReapplyAllAsync"/> call on this (scoped) instance.
    /// Pass null to detach. Used by the background apply runner to stream progress.
    /// </summary>
    void SetApplyProgress(IApplyProgressSink? sink);

    /// <summary>Applies a patch. Must be the next incremental patch after the current level.</summary>
    Task<ApplyPatchResultDto> ApplyPatchAsync(string stackId, string patchKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-applies the full content of every already-applied patch (level 1..current), in order, on top
    /// of the standard AzerothCore updates: the DBC set is fetched once from the server, each patch's
    /// DBC CSVs and SQL and maps are (re)applied cumulatively, then patch-D.MPQ is rebuilt and all DBCs
    /// and MPQ overlay files are re-published to clients. Use after a core update/rebuild may have
    /// overwritten custom SQL/DBC/MPQ content.
    /// </summary>
    Task<ApplyPatchResultDto> ReapplyAllAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies standard AzerothCore database updates (including module SQL shipped in the stack's
    /// db-import image). Idempotent and safe to run after a recompile when no custom patches need
    /// reapplying.
    /// </summary>
    Task<ApplyPatchResultDto> ApplyStandardDbUpdatesAsync(string stackId, CancellationToken cancellationToken = default);

    // ===== File operations =====

    /// <summary>
    /// Uploads a file into a patch category. Enforces per-category extension rules. For the <c>mpq</c>
    /// category a non-empty <paramref name="description"/> of the archive's contents is required.
    /// </summary>
    Task<PatchFileDto> UploadFileAsync(string stackId, string patchKey, string category, string fileName, Stream content, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>Reads the text content of a DBC (.txt) file for inline editing.</summary>
    Task<string> ReadDbcFileAsync(string stackId, string patchKey, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Saves edited text content back to a DBC (.txt) file.</summary>
    Task SaveDbcFileAsync(string stackId, string patchKey, string fileName, string content, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file from a patch category.</summary>
    Task DeleteFileAsync(string stackId, string patchKey, string category, string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the client MPQ files currently published to the stack's client overlay (i.e. the archives
    /// previously created/published by patches and served to players). Used to let an author pick which
    /// of them a new patch should remove.
    /// </summary>
    Task<List<PublishedMpqDto>> GetPublishedMpqsAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the list of published MPQ file names that a patch removes from the client overlay when
    /// applied. The removal runs before the patch publishes any new MPQ files.
    /// </summary>
    Task SetMpqRemovalsAsync(string stackId, string patchKey, IReadOnlyList<string> fileNames, CancellationToken cancellationToken = default);
}
