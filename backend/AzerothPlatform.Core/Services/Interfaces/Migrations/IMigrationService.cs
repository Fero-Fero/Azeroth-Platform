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

    /// <summary>
    /// Returns config overrides for a patch enriched with current values from the stack's live
    /// server <c>.conf</c> files.
    /// </summary>
    Task<List<PatchConfigOverrideDto>> GetPatchConfigOverridesPreviewAsync(
        string stackId,
        string patchKey,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the player-facing news article bundled in a patch folder, if any.</summary>
    Task<PatchNewsPreviewDto> GetPatchNewsPreviewAsync(
        string stackId,
        string patchKey,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a patch news cover image on disk for preview, or null when absent.</summary>
    Task<(string Path, string ContentType)?> ResolvePatchNewsCoverAsync(
        string stackId,
        string patchKey,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves an inline image or other asset from a patch's news folder for preview.</summary>
    Task<(string Path, string ContentType)?> ResolvePatchNewsAssetAsync(
        string stackId,
        string patchKey,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>Saves description.md / description.txt for a patch.</summary>
    Task<PatchDetailsDto> SavePatchDescriptionAsync(string stackId, string patchKey, string content, CancellationToken cancellationToken = default);

    /// <summary>Saves a patch news article (<c>news/article.json</c> + <c>news/article.html</c>).</summary>
    Task<PatchDetailsDto> SavePatchNewsAsync(
        string stackId,
        string patchKey,
        SavePatchNewsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Uploads or replaces the patch news cover image.</summary>
    Task<PatchDetailsDto> UploadPatchNewsCoverAsync(
        string stackId,
        string patchKey,
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>Writes <c>config/launcher.json</c> theme override for a patch.</summary>
    Task<PatchDetailsDto> SavePatchLauncherThemeAsync(
        string stackId,
        string patchKey,
        string theme,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a new patch folder ("{level}_{name}") with the standard sub-folders.</summary>
    Task<PatchSummaryDto> CreatePatchAsync(string stackId, CreatePatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a patch collection archive. Override preserves indices from the archive; append assigns
    /// the next patch index (1.x, 2.x, 3.x) per expansion. Patch folders must be named
    /// <c>patch {index}</c> or <c>patch {index} {name}</c>.
    /// </summary>
    Task<ImportPatchCollectionResultDto> ImportPatchCollectionAsync(string stackId, Stream zipContent, string mode, CancellationToken cancellationToken = default);

    /// <summary>Lists one directory level of the stack's patch folder tree.</summary>
    Task<ClientBrowseResultDto> BrowsePatchFilesAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Deletes an unapplied patch file or folder from the stack's patch folder tree.</summary>
    Task DeletePatchEntryAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Deletes every patch folder under migrations/. Refused once any patch has been applied.</summary>
    Task<int> DeleteAllPatchesAsync(string stackId, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Extracts live <c>/data/dbc</c> into <c>server_dbc/</c> when the volume exists.
    /// Returns false when the stack has not populated client-data yet (deposit should be deferred).
    /// </summary>
    Task<bool> TryEnsureServerDbcBaselineAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Copies named DBC files from <c>server_dbc/</c> into the live data volume.</summary>
    Task PushServerDbcFilesAsync(
        string stackId,
        IReadOnlyList<string> dbcFileNames,
        CancellationToken cancellationToken = default);

    /// <summary>Rebuilds <c>patch-D.MPQ</c> from the current <c>server_dbc/</c> set and publishes it.</summary>
    Task RebuildPatchDAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies every SQL file against one AzerothCore database in a single transaction
    /// (world, then auth, then characters — callers sequence the three databases).
    /// </summary>
    Task ApplySqlFilesAsync(
        string stackId,
        string database,
        IReadOnlyList<string> sqlFilePaths,
        CancellationToken cancellationToken = default);

    /// <summary>Copies an MPQ into the client overlay Data/ folder, pushes the overlay, and rescans.</summary>
    Task PublishOverlayMpqAsync(string stackId, string mpqPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies files into a stack data-volume subfolder (<c>maps</c>, <c>mmaps</c>, or <c>vmaps</c>),
    /// flattening to the filename so they land at <c>/data/{subdir}/...</c>.
    /// </summary>
    Task PublishDataVolumeFilesAsync(
        string stackId,
        string volumeSubdir,
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken = default);
}
