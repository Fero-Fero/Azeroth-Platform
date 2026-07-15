namespace AzerothPlatform.Launcher.Models;

/// <summary>A single selectable server profile, mirroring the backend LauncherProfileDto.</summary>
public sealed class LauncherProfile
{
    public string StackId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string RealmlistHost { get; set; } = string.Empty;
    public int RealmlistPort { get; set; }

    /// <summary>Host port of the per-stack armory web app, or 0 when it isn't available.</summary>
    public int ArmoryPort { get; set; }

    /// <summary>
    /// Absolute base URL of this stack's own portal/client container (portal + manifest + files +
    /// launcher + login). Set in stack-portal mode; blank in legacy manager mode. This is the "known
    /// server" the launcher persists and reconciles against.
    /// </summary>
    public string PortalUrl { get; set; } = string.Empty;

    /// <summary>Whether this stack's portal answered a /health ping during the last reconcile.</summary>
    public bool Healthy { get; set; } = true;

    public string? BackgroundUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? NewsUrl { get; set; }

    /// <summary>Effective style template id for this profile (per-stack override, else global).</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>Effective accent color (hex) applied when this profile is selected; empty = global.</summary>
    public string AccentColor { get; set; } = string.Empty;

    /// <summary>Informational WoW client version label for this profile (per-stack, else global).</summary>
    public string ClientVersion { get; set; } = string.Empty;

    /// <summary>Folder name (under Data/ and in caches) used to stash this profile's overlay content.</summary>
    public string FolderName => string.IsNullOrWhiteSpace(StackId) ? "default" : StackId;

    public override string ToString() => DisplayName;
}

/// <summary>A single rich news article fetched from the backend news list (mirrors LauncherNewsItemDto).</summary>
public sealed class LauncherNewsDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool HasImage { get; set; }
    public string? ImageUrl { get; set; }
}

/// <summary>
/// The runtime profiles document fetched from /api/launcher/profiles: global branding + every
/// visible profile. Mirrors the backend LauncherProfilesDto.
/// </summary>
public sealed class LauncherProfilesResponse
{
    public string AppName { get; set; } = "Azeroth Platform";
    public string BrandingTitle { get; set; } = "Azeroth Platform Launcher";
    public string GameExecutable { get; set; } = "Wow.exe";
    public string LaunchArguments { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = string.Empty;
    public string BaseManifestUrl { get; set; } = "/api/launcher/manifest";
    public string? DefaultBackgroundUrl { get; set; }
    public string? DefaultLogoUrl { get; set; }
    public string? GlobalNewsUrl { get; set; }

    /// <summary>Selected style template id (classic/tbc/wotlk).</summary>
    public string Template { get; set; } = "wotlk";

    /// <summary>Accent color (hex) from the selected template, applied to the launcher UI.</summary>
    public string AccentColor { get; set; } = string.Empty;

    public List<LauncherProfile> Profiles { get; set; } = new();
}

/// <summary>Status of the currently-available compiled launcher, for self-update checks.</summary>
public sealed class LauncherBuildStatus
{
    public string? AvailableVersion { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the available launcher exe, received over the trusted (TLS) manager
    /// channel and verified against the downloaded self-update before it replaces the running exe.
    /// </summary>
    public string? AvailableSha256 { get; set; }

    public bool DownloadAvailable { get; set; }
}

/// <summary>A launcher login request posted to /api/launcher/login (mirrors LauncherLoginRequestDto).</summary>
public sealed class LauncherLoginRequest
{
    public string StackId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>The result of a launcher login attempt (mirrors LauncherLoginResponseDto).</summary>
public sealed class LauncherLoginResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}
