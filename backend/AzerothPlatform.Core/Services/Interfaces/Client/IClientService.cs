using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Manages the per-stack BASE WoW client that a stack's client container serves as its read-only base
/// layer: uploading/extracting a base client archive into that stack's base directory and reporting on
/// its current state. Each stack has its own base, so the client is uploaded per stack. Per-stack
/// overlays (patch MPQs) are handled by the migration pipeline.
/// </summary>
public interface IClientService
{
    /// <summary>Returns a summary of the currently uploaded base client for a stack (existence, size, sanity checks).</summary>
    Task<ClientBaseInfoDto> GetBaseInfoAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts an uploaded base-client archive (zip, rar, 7z, tar/tar.gz, …; the format is detected
    /// from the content) into the stack's base client volume, validates it looks like a WoW install, and
    /// removes any file the archive did not contain (platform-managed content excepted), so the volume
    /// ends up matching the archive rather than accumulating the leftovers of previous uploads.
    /// Returns the new base info.
    /// </summary>
    Task<ClientBaseInfoDto> UploadBaseClientAsync(string stackId, Stream archiveStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parks an uploaded archive and returns a <see cref="StagedClientArchive"/> token for it. The bytes
    /// stream into a throwaway Docker volume on the stack's engine (RAR excepted, which the engine-side
    /// extractor cannot open and so lands on manager disk), never into a manager-side copy of the whole
    /// client. Extract and volume seed happen later via <see cref="InstallStagedBaseClientAsync"/> so
    /// the HTTP request returns as soon as the bytes are in and the manager stays responsive.
    /// </summary>
    Task<string> StageBaseClientArchiveAsync(string stackId, Stream archiveStream, CancellationToken cancellationToken = default);

    /// <summary>Installs a staged archive into the stack client-base volume, then discards the staging.</summary>
    Task<ClientBaseInfoDto> InstallStagedBaseClientAsync(string stackId, string stagingToken, CancellationToken cancellationToken = default);

    /// <summary>Releases staging (work volume or temp folder) when install was not queued.</summary>
    Task DiscardStagedBaseClientArchiveAsync(string stackId, string stagingToken);

    /// <summary>
    /// Downloads a base-client URL (archive or public Google Drive folder) into the stack using the
    /// same extract/seed path as upload. When <paramref name="url"/> is blank, uses the configured
    /// <c>ClientDownload:BaseClientUrl</c> (Express auto-provision). Throws when no URL is available.
    /// </summary>
    Task<ClientBaseInfoDto> DownloadBaseClientAsync(
        string stackId,
        string? url = null,
        CancellationToken cancellationToken = default);

    /// <summary>Re-seeds the stack's base client volume from its base directory so a running stack picks up changes.</summary>
    Task<ClientBaseInfoDto> RescanBaseAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties everything the stack serves to launchers so a broken client can be rebuilt from scratch:
    /// the base volume, the overlay volume, the manager-side overlay mirror the next patch apply would
    /// re-seed from, and the client container's hash cache, manifest snapshot and verify token.
    ///
    /// Leaves the built launcher, the portal registry, branding and news alone — those are stack
    /// identity, not client content. Recovery is to re-upload a base client and reapply the patches.
    /// </summary>
    Task<ClientBaseInfoDto> PurgeClientContentAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists one directory level of a stack's base client tree so admins can navigate it and confirm
    /// files are present. <paramref name="relativePath"/> is relative to the base root ('' = root); it is
    /// validated to stay within the base directory (no traversal). Returns sub-directories first, then files.
    /// </summary>
    Task<ClientBrowseResultDto> BrowseAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file or (recursively) a folder within a stack's base client, then re-seeds the base
    /// client volume so a running stack reflects the removal. <paramref name="relativePath"/> is relative
    /// to the base root and validated to stay within it (no traversal); deleting the root is rejected.
    /// Returns the updated base info.
    /// </summary>
    Task<ClientBaseInfoDto> DeleteEntryAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a single uploaded file into a folder within a stack's base client (creating the folder if
    /// needed), then re-seeds the base client volume. <paramref name="relativeDir"/> is the destination
    /// folder relative to the base root ('' = root) and validated to stay within it (no traversal);
    /// <paramref name="fileName"/> is the (sanitized) file name. Returns the updated base info.
    /// </summary>
    Task<ClientBaseInfoDto> UploadFileAsync(
        string stackId, string relativeDir, string fileName, Stream content, CancellationToken cancellationToken = default);
}
