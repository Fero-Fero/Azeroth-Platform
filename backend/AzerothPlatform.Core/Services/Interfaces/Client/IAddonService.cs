using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Manages WoW addons served to the launcher. Addons live under a client root's
/// <c>game/Interface/AddOns/</c> directory and are distributed through the normal client manifest
/// (they are "managed" files, so the launcher auto-installs, updates, and prunes them).
/// A non-empty <c>stackId</c> targets that stack's client.
/// </summary>
public interface IAddonService
{
    /// <summary>Lists the addons currently served for the given scope.</summary>
    Task<AddonListDto> ListAsync(string? stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds/updates addons from an uploaded <c>.zip</c> archive (the archive's folders are extracted
    /// into <c>Interface/AddOns/</c>), then rescans the client manifest. Returns the updated listing.
    /// </summary>
    Task<AddonListDto> UploadZipAsync(string? stackId, string fileName, Stream zipContent, CancellationToken cancellationToken = default);

    /// <summary>Deletes an addon by folder name, then rescans the client manifest.</summary>
    Task<AddonListDto> DeleteAsync(string? stackId, string addonName, CancellationToken cancellationToken = default);

    /// <summary>
    /// The static addon catalog (built-in entries) with install status computed for the given scope.
    /// </summary>
    Task<IReadOnlyList<AddonCatalogEntryDto>> GetCatalogAsync(string? stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every built-in catalog entry, unfiltered by stack modules or server type. Used by the wizard
    /// to resolve recommended addon ids before a stack exists.
    /// </summary>
    IReadOnlyList<AddonCatalogEntryDto> GetCatalogDefinitions();

    /// <summary>
    /// Installs a catalog addon by id: downloads its <c>.zip</c> server-side and extracts the contained
    /// addon folder(s) into <c>Interface/AddOns/</c>, then rescans the client manifest.
    /// </summary>
    Task<AddonListDto> InstallFromCatalogAsync(string? stackId, string addonId, CancellationToken cancellationToken = default);
}
