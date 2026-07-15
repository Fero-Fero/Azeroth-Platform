using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Manages a stack's operator-uploaded armory asset bundles: the 3D model-viewer dataset
/// (armory.data.zip + armory.textures.zip) and the static web assets (armory.static.zip). Each stack
/// has its own bundles, so armory data is uploaded per stack. Uploads are extracted into a persistent
/// location that takes precedence over the assets baked into the manager image.
/// </summary>
public interface IArmoryAssetsService
{
    /// <summary>Returns the default styling palette for each template (Classic, TBC, WotLK, Custom).</summary>
    Dictionary<string, ArmoryStylingDto> GetStylingDefaults();

    /// <summary>Returns the resolved widget arrangement for a given page + template combination.</summary>
    ArmoryPageLayoutDto GetPageTemplate(string pageId, string templateId);

    /// <summary>Returns a summary of what has been uploaded for a stack.</summary>
    Task<ArmoryAssetsInfoDto> GetInfoAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Returns the stack's armory theme settings.</summary>
    Task<ArmoryStylingDto> GetStylingAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Saves armory theme settings and writes the generated CSS override for the next image rebuild.</summary>
    Task<ArmoryStylingDto> SaveStylingAsync(string stackId, ArmoryStylingDto styling, CancellationToken cancellationToken = default);

    /// <summary>Stores a wallpaper image for the stack's armory theme and updates the generated CSS override.</summary>
    Task<ArmoryStylingDto> UploadWallpaperAsync(
        string stackId, string fileName, Stream content, string? contentType = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the on-disk path and MIME type of the stack's uploaded custom wallpaper, if any.</summary>
    (string Path, string ContentType)? TryGetWallpaperFile(string stackId);

    /// <summary>Returns the stack's armory homepage layout settings.</summary>
    Task<ArmoryLayoutDto> GetLayoutAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Saves armory layout settings and writes the generated layout CSS for the next image rebuild.</summary>
    Task<ArmoryLayoutDto> SaveLayoutAsync(string stackId, ArmoryLayoutDto layout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts an uploaded model-viewer bundle (armory.data.zip / armory.textures.zip; any archive
    /// format) into the stack's dataset directory, merging with anything already there, then refreshes
    /// the stack's assets volume so a running stack picks it up.
    /// </summary>
    Task<ArmoryAssetsInfoDto> UploadDataAsync(string stackId, Stream archiveStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts an uploaded static bundle (armory.static.zip; any archive format) into the stack's static
    /// directory, merging with anything already there, and marks the stack's armory image as needing a rebuild.
    /// </summary>
    Task<ArmoryAssetsInfoDto> UploadStaticAsync(string stackId, Stream archiveStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes uploaded static web assets while preserving the model-viewer dataset and generated styling assets.
    /// </summary>
    Task<ArmoryAssetsInfoDto> DeleteStaticAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Clears the stack's "static rebuild pending" marker (called after its armory image is rebuilt).</summary>
    Task ClearStaticRebuildPendingAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists one directory level of a stack's uploaded model-viewer dataset so admins can navigate it and
    /// confirm files are present. <paramref name="relativePath"/> is relative to the dataset root ('' = root);
    /// it is validated to stay within the dataset directory (no traversal). Sub-directories first, then files.
    /// </summary>
    Task<ClientBrowseResultDto> BrowseDataAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file or (recursively) a folder within a stack's uploaded model-viewer dataset, then
    /// refreshes the stack's assets volume so a running stack reflects the removal.
    /// <paramref name="relativePath"/> is relative to the dataset root and validated to stay within it
    /// (no traversal); deleting the root is rejected. Returns the updated asset info.
    /// </summary>
    Task<ArmoryAssetsInfoDto> DeleteDataAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a single uploaded file into a folder of a stack's model-viewer dataset (creating the folder
    /// if needed), then refreshes the stack's assets volume. <paramref name="relativeDir"/> is the
    /// destination folder relative to the dataset root ('' = root) and validated to stay within it (no
    /// traversal); <paramref name="fileName"/> is the (sanitized) file name. Returns the updated asset info.
    /// </summary>
    Task<ArmoryAssetsInfoDto> UploadDataFileAsync(
        string stackId, string relativeDir, string fileName, Stream content, CancellationToken cancellationToken = default);
}
