namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// A single addon served to the launcher. An addon is a folder under the client's
/// <c>game/Interface/AddOns/</c> directory (e.g. <c>game/Interface/AddOns/Questie/</c>).
/// </summary>
public sealed class AddonSummaryDto
{
    /// <summary>Addon folder name (as it appears under Interface/AddOns).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Number of files the addon contains (recursive).</summary>
    public int FileCount { get; set; }

    /// <summary>Total size of the addon's files in bytes.</summary>
    public long TotalSize { get; set; }

    /// <summary>Whether this addon matches a recommended catalog entry.</summary>
    public bool Recommended { get; set; }
}

/// <summary>
/// A catalog addon an admin can install with one click (mirrors the module catalog). Built-in
/// entries are defined in code; the download is a <c>.zip</c> fetched server-side and extracted into
/// the client's <c>Interface/AddOns/</c>.
/// </summary>
public sealed class AddonCatalogEntryDto
{
    /// <summary>Stable catalog id (used in the install URL).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short description of what the addon does.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Grouping category (e.g. Quests, UI, Raiding).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Direct <c>.zip</c> download URL fetched server-side on install.</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>Optional project/home page for the addon.</summary>
    public string? Website { get; set; }

    /// <summary>Whether this is a built-in catalog entry.</summary>
    public bool IsBuiltIn { get; set; } = true;

    /// <summary>Addon folder name(s) this entry installs, used to report install status.</summary>
    public List<string> Folders { get; set; } = new();

    /// <summary>Whether one of this entry's folders is currently present in the client.</summary>
    public bool Installed { get; set; }

    /// <summary>Whether this addon is recommended for most players.</summary>
    public bool Recommended { get; set; }

    /// <summary>
    /// Stack module ids that make this addon a contextual suggestion (computed per stack in
    /// <see cref="IAddonService.GetCatalogAsync"/>).
    /// </summary>
    public List<string> RelatedModuleIds { get; set; } = new();

    /// <summary>
    /// True when a related stack module is installed and this addon is not yet present.
    /// Only set for stack-scoped catalog requests.
    /// </summary>
    public bool Suggested { get; set; }

    /// <summary>
    /// When set, the extracted addon folder is installed under this name instead of the archive
    /// folder name (e.g. GitHub zips that do not match the WoW AddOns folder name).
    /// </summary>
    public string? InstallAsFolder { get; set; }
}

/// <summary>
/// Listing of the addons currently served for a client root (global or per-stack).
/// </summary>
public sealed class AddonListDto
{
    /// <summary>Whether this listing is for the global client (<c>false</c>) or a stack (<c>true</c>).</summary>
    public bool IsStackScoped { get; set; }

    /// <summary>Stack id when <see cref="IsStackScoped"/> is true; otherwise null.</summary>
    public string? StackId { get; set; }

    public List<AddonSummaryDto> Addons { get; set; } = new();

    /// <summary>Total size of all addons in bytes.</summary>
    public long TotalSize { get; set; }
}
