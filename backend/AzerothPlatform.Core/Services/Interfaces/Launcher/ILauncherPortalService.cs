using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Kinds of branding assets stored per profile / globally.
/// </summary>
public enum LauncherAssetKind
{
    Background,
    Logo,

    /// <summary>Global-only app icon (.ico) baked into the compiled launcher; not valid per-stack.</summary>
    Icon
}

/// <summary>
/// Manages website-editable launcher distribution config: global branding + per-stack profiles,
/// their branding assets, and the aggregated profiles document served to launchers.
/// </summary>
public interface ILauncherPortalService
{
    // ===== Global config =====
    Task<LauncherDistributionConfigDto> GetConfigAsync(CancellationToken cancellationToken = default);

    Task<LauncherDistributionConfigDto> SaveConfigAsync(LauncherDistributionConfigDto config, CancellationToken cancellationToken = default);

    /// <summary>Stores a global branding asset from a stream. Returns the updated config.</summary>
    Task<LauncherDistributionConfigDto> SaveGlobalAssetAsync(LauncherAssetKind kind, string fileName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Resolves a global asset to an absolute path + content type, or null when unset.</summary>
    (string Path, string ContentType)? ResolveGlobalAsset(LauncherAssetKind kind);

    // ===== Hard-coded style templates =====

    /// <summary>The hard-coded launcher style templates (classic/tbc/wotlk) with resolved asset URLs.</summary>
    IReadOnlyList<LauncherTemplateDto> GetTemplates();

    /// <summary>Resolves a template asset ("background"/"logo") to an absolute path + content type, or null.</summary>
    (string Path, string ContentType)? ResolveTemplateAsset(string templateId, string asset);

    // ===== Global news =====

    /// <summary>
    /// Returns the global news articles (fallback shown when a profile has no news). When
    /// <paramref name="includeDrafts"/> is false (the launcher-facing default) draft articles are
    /// omitted; the website editor passes true to see and manage drafts.
    /// </summary>
    Task<IReadOnlyList<LauncherNewsItemDto>> GetGlobalNewsAsync(bool includeDrafts = false, CancellationToken cancellationToken = default);

    /// <summary>Saves the global news list (sanitizes HTML, prunes orphan cover images). Returns the stored list.</summary>
    Task<IReadOnlyList<LauncherNewsItemDto>> SaveGlobalNewsAsync(IReadOnlyList<LauncherNewsItemDto> items, CancellationToken cancellationToken = default);

    /// <summary>Stores/replaces a global news article's cover image. Returns the updated list.</summary>
    Task<IReadOnlyList<LauncherNewsItemDto>> SaveGlobalNewsImageAsync(string itemId, string fileName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Resolves a global news cover image to an absolute path + content type, or null.</summary>
    (string Path, string ContentType)? ResolveGlobalNewsImage(string itemId);

    /// <summary>
    /// Broadcasts the published global news feed to every launcher-visible stack: each published global
    /// article (with its cover image) is copied into the stack's own news store under a reserved
    /// <c>global-</c> id so re-broadcasting refreshes rather than duplicates. The stack handles placement
    /// on upload — broadcast articles are automatically assigned the highest sort orders so they land as
    /// the stack's latest news, with no manual reordering needed. Best-effort per stack.
    /// </summary>
    Task<GlobalNewsBroadcastResult> BroadcastGlobalNewsAsync(CancellationToken cancellationToken = default);

    // ===== Per-stack news =====

    /// <summary>
    /// Returns a stack's news articles. When <paramref name="includeDrafts"/> is false (the
    /// launcher-facing default) draft articles are omitted; the website editor passes true.
    /// </summary>
    Task<IReadOnlyList<LauncherNewsItemDto>> GetStackNewsAsync(string stackId, bool includeDrafts = false, CancellationToken cancellationToken = default);

    /// <summary>Saves a stack's news list (sanitizes HTML, prunes orphan cover images). Returns the stored list.</summary>
    Task<IReadOnlyList<LauncherNewsItemDto>> SaveStackNewsAsync(string stackId, IReadOnlyList<LauncherNewsItemDto> items, CancellationToken cancellationToken = default);

    /// <summary>Stores/replaces a stack news article's cover image. Returns the updated list.</summary>
    Task<IReadOnlyList<LauncherNewsItemDto>> SaveStackNewsImageAsync(string stackId, string itemId, string fileName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or replaces a single news article in a stack's news feed (used when a patch with
    /// <c>news/article.json</c> is applied).
    /// </summary>
    Task<IReadOnlyList<LauncherNewsItemDto>> MergeStackNewsArticleAsync(
        string stackId,
        LauncherNewsItemDto article,
        CancellationToken cancellationToken = default);

    /// <summary>Copies a patch news cover image into the stack news store for the given article id.</summary>
    Task MergeStackNewsCoverFromFileAsync(
        string stackId,
        string itemId,
        string sourceImagePath,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a stack news cover image to an absolute path + content type, or null.</summary>
    Task<(string Path, string ContentType)?> ResolveStackNewsImageAsync(string stackId, string itemId, CancellationToken cancellationToken = default);

    // ===== Aggregated profiles document (consumed by the launcher) =====
    Task<LauncherProfilesDto> GetProfilesAsync(CancellationToken cancellationToken = default);

    // ===== Per-stack profile config =====
    Task<LauncherProfileConfigDto> GetProfileAsync(string stackId, CancellationToken cancellationToken = default);

    Task<LauncherProfileConfigDto> SaveProfileAsync(LauncherProfileConfigDto profile, CancellationToken cancellationToken = default);

    Task<LauncherProfileConfigDto> SaveProfileAssetAsync(string stackId, LauncherAssetKind kind, string fileName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a stack's uploaded wallpaper/logo override so the launcher falls back to the global
    /// theme's asset again. Returns the updated profile config.
    /// </summary>
    Task<LauncherProfileConfigDto> DeleteProfileAssetAsync(string stackId, LauncherAssetKind kind, CancellationToken cancellationToken = default);

    /// <summary>Resolves a per-stack asset (background/logo/news) to an absolute path + content type.</summary>
    Task<(string Path, string ContentType)?> ResolveProfileAssetAsync(string stackId, string asset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the <em>effective</em> launcher branding asset (background/logo) a stack should serve:
    /// the per-stack upload when present, otherwise the global default (uploaded global asset, else the
    /// selected theme's shipped asset). Returns null when no effective asset exists. Used by the manager
    /// to push each stack's branding into its own client container.
    /// </summary>
    Task<(string Path, string ContentType)?> ResolveEffectiveProfileAssetAsync(string stackId, LauncherAssetKind kind, CancellationToken cancellationToken = default);
}
