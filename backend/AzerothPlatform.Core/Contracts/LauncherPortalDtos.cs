namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Global, website-editable launcher distribution settings. These are baked into the compiled
/// launcher's identity (app/install name, backend URL, branding) and/or served at runtime so the
/// launcher can render default branding before a profile is selected.
/// </summary>
public sealed class LauncherDistributionConfigDto
{
    /// <summary>
    /// Display name and install-folder name. The launcher installs the shared client to
    /// <c>C:/Program Files/{AppName}</c>.
    /// </summary>
    public string AppName { get; set; } = "Azeroth Platform";

    /// <summary>Public backend base URL the launcher fetches profiles/files from (baked at compile).</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Window/branding title shown before a profile is selected.</summary>
    public string BrandingTitle { get; set; } = "Azeroth Platform Launcher";

    /// <summary>Executable started after syncing, relative to the install folder.</summary>
    public string GameExecutable { get; set; } = "Wow.exe";

    /// <summary>Extra CLI args passed to the game.</summary>
    public string LaunchArguments { get; set; } = string.Empty;

    /// <summary>Informational client version label.</summary>
    public string ClientVersion { get; set; } = "3.3.5a (12340)";

    /// <summary>Whether a default background asset has been uploaded.</summary>
    public bool HasBackground { get; set; }

    /// <summary>Whether a default logo asset has been uploaded.</summary>
    public bool HasLogo { get; set; }

    /// <summary>
    /// Whether a global app icon (.ico) has been uploaded. This is a universal (not per-stack)
    /// setting baked as the launcher's Windows exe icon and window/taskbar icon at compile time.
    /// </summary>
    public bool HasIcon { get; set; }

    /// <summary>
    /// Selected hard-coded style template id (<c>classic</c>, <c>tbc</c>, <c>wotlk</c>). Drives the
    /// launcher's accent color and default background/logo when nothing more specific is set.
    /// </summary>
    public string Template { get; set; } = "wotlk";

    /// <summary>
    /// When true, the compiled launcher shows a login screen and requires the player to authenticate
    /// with a game account (verified against the selected profile's auth database) before they can
    /// download or play. Baked into the launcher at build time, so changing it requires a new build.
    /// </summary>
    public bool RequireLogin { get; set; }
}

/// <summary>
/// A launcher login attempt: the profile (stack) whose auth database to check plus the game account
/// credentials. Posted by the desktop launcher to <c>POST /api/launcher/login</c>.
/// </summary>
public sealed class LauncherLoginRequestDto
{
    public string StackId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

/// <summary>Result of a launcher login attempt.</summary>
public sealed class LauncherLoginResponseDto
{
    public bool Success { get; set; }

    /// <summary>A player-facing reason when <see cref="Success"/> is false; null on success.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// A single rich news / patch-notes article: an optional cover image, a headline, a date, and an
/// HTML body (WYSIWYG-authored, sanitized on save). Used both for website editing and for the news
/// list served to the launcher.
/// </summary>
public sealed class LauncherNewsItemDto
{
    /// <summary>Stable id (used for the cover-image filename and ordering).</summary>
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Display date (ISO <c>yyyy-MM-dd</c> or free text) shown on the card.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Sanitized HTML body rendered in the reading view / website preview.</summary>
    public string Html { get; set; } = string.Empty;

    /// <summary>
    /// Optional content category shown as a colored corner ribbon on the news cards
    /// (e.g. <c>patch</c>, <c>announcement</c>, <c>expansion</c>, <c>event</c>, <c>update</c>,
    /// <c>hotfix</c>). Empty means no ribbon. Normalized to a known lowercase token on save.
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Ascending order in the news strip / grid.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Draft flag. Draft articles are saved and visible in the website editor but are withheld from
    /// the launcher-facing news feed until published (draft = false).
    /// </summary>
    public bool IsDraft { get; set; }

    /// <summary>Whether a cover image has been uploaded (admin editing view).</summary>
    public bool HasImage { get; set; }

    /// <summary>Relative backend URL to the cover image, or null when none is set.</summary>
    public string? ImageUrl { get; set; }
}

/// <summary>
/// Outcome of broadcasting the global news feed to every launcher-visible stack. Global articles are
/// copied into each stack's own news store (which the stack then serves) and automatically placed as
/// that stack's latest news, so an announcement written once reaches every stack without manual
/// duplication or reordering.
/// </summary>
public sealed class GlobalNewsBroadcastResult
{
    /// <summary>Number of published global articles that were pushed to each stack.</summary>
    public int ArticleCount { get; set; }

    /// <summary>Total launcher-visible stacks the broadcast targeted.</summary>
    public int TotalStacks { get; set; }

    /// <summary>How many stacks received the broadcast successfully.</summary>
    public int Updated { get; set; }

    /// <summary>Per-stack failure messages ("stackId: reason") for stacks that could not be updated.</summary>
    public List<string> Failures { get; set; } = new();
}

/// <summary>A hard-coded launcher style template (per-expansion look) selectable on the website.</summary>
public sealed class LauncherTemplateDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Accent color (hex, e.g. <c>#C8A24B</c>) applied to the launcher and preview.</summary>
    public string AccentColor { get; set; } = string.Empty;

    /// <summary>Relative backend path to the template's shipped background asset, or null.</summary>
    public string? BackgroundUrl { get; set; }

    /// <summary>Relative backend path to the template's shipped logo asset, or null.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Relative backend path to the template's shipped app icon (.ico), or null.</summary>
    public string? IconUrl { get; set; }
}

/// <summary>
/// A single selectable server profile in the launcher's dropdown, derived from a visible stack plus
/// its website-configured branding.
/// </summary>
public sealed class LauncherProfileDto
{
    public string StackId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public string RealmlistHost { get; set; } = string.Empty;

    public int RealmlistPort { get; set; }

    /// <summary>
    /// Host port the per-stack armory web app is published on, or 0 when the armory isn't enabled.
    /// The launcher builds the armory URL from its configured server host + this port.
    /// </summary>
    public int ArmoryPort { get; set; }

    /// <summary>Relative backend path for this profile's background asset, or null when unset.</summary>
    public string? BackgroundUrl { get; set; }

    /// <summary>Relative backend path for this profile's logo asset, or null when unset.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Relative backend path to this profile's news list (JSON), or null when it has none.</summary>
    public string? NewsUrl { get; set; }

    /// <summary>
    /// Effective style template id for this profile (per-stack override, else the global template),
    /// or empty when none. Informational for the launcher.
    /// </summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>
    /// Effective accent color (hex) for this profile, applied to the launcher UI when the profile is
    /// selected. Empty falls back to the global accent.
    /// </summary>
    public string AccentColor { get; set; } = string.Empty;

    /// <summary>
    /// Informational WoW client version label for this profile (per-stack value, else the global
    /// default). Shown by the launcher when this profile is selected.
    /// </summary>
    public string ClientVersion { get; set; } = string.Empty;
}

/// <summary>
/// The document the launcher fetches at runtime: global branding plus every visible profile. New
/// stacks appear here automatically (no recompile), satisfying the "updatable when new stacks are
/// available" requirement.
/// </summary>
public sealed class LauncherProfilesDto
{
    public string AppName { get; set; } = "Azeroth Platform";

    public string BrandingTitle { get; set; } = "Azeroth Platform Launcher";

    public string GameExecutable { get; set; } = "Wow.exe";

    public string LaunchArguments { get; set; } = string.Empty;

    public string ClientVersion { get; set; } = "3.3.5a (12340)";

    /// <summary>Relative backend path to the shared base client manifest.</summary>
    public string BaseManifestUrl { get; set; } = "/api/launcher/manifest";

    /// <summary>Relative backend path for the default background asset, or null.</summary>
    public string? DefaultBackgroundUrl { get; set; }

    /// <summary>Relative backend path for the default logo asset, or null.</summary>
    public string? DefaultLogoUrl { get; set; }

    /// <summary>Relative backend path for the global news XML, or null.</summary>
    public string? GlobalNewsUrl { get; set; }

    /// <summary>Selected style template id (classic/tbc/wotlk).</summary>
    public string Template { get; set; } = "wotlk";

    /// <summary>Accent color (hex) from the selected template, applied to the launcher UI.</summary>
    public string AccentColor { get; set; } = string.Empty;

    public List<LauncherProfileDto> Profiles { get; set; } = new();
}

/// <summary>Per-stack launcher profile settings edited on the website.</summary>
public sealed class LauncherProfileConfigDto
{
    public string StackId { get; set; } = string.Empty;

    /// <summary>Whether this stack appears as a selectable profile in the launcher.</summary>
    public bool Visible { get; set; }

    /// <summary>Display name in the dropdown (falls back to realm/stack name when blank).</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>Realmlist host override; blank uses the deployment-wide realmlist host.</summary>
    public string RealmlistHostOverride { get; set; } = string.Empty;

    /// <summary>Effective realmlist host (resolved), for display.</summary>
    public string EffectiveRealmlistHost { get; set; } = string.Empty;

    /// <summary>Realmlist port (the stack's auth server port), for display.</summary>
    public int RealmlistPort { get; set; }

    /// <summary>
    /// Informational WoW client version label for this stack (e.g. <c>3.3.5a (12340)</c>). Shown in
    /// the launcher when this profile is selected. Blank falls back to the global default.
    /// </summary>
    public string ClientVersion { get; set; } = string.Empty;

    /// <summary>Whether this stack has uploaded a wallpaper that overrides the global theme's background.</summary>
    public bool HasBackground { get; set; }

    /// <summary>Whether this stack has uploaded a logo that overrides the global theme's logo.</summary>
    public bool HasLogo { get; set; }
}
