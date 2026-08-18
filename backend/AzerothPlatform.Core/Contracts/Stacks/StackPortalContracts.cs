namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// A single stack's advertised connection info, as replicated across every stack so a launcher can keep
/// its multi-stack list without the manager. Each stack serves the full registry from its own container
/// at <c>GET /portal</c>; the launcher reconciles the copies it gathers, keeping the newest
/// <see cref="Revision"/> per <see cref="StackId"/> and dropping unreachable/duplicate entries.
/// </summary>
public sealed class StackRegistryEntry
{
    /// <summary>Stable stack id (reconciliation key).</summary>
    public string StackId { get; set; } = string.Empty;

    /// <summary>Player-facing display name for the realm/profile.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Absolute base URL of this stack's own client/portal container (e.g. <c>http://host:8101</c>). The
    /// launcher fetches <c>/portal</c>, <c>/manifest</c>, <c>/files/*</c>, <c>/launcher/*</c> and
    /// <c>/login</c> from here. This is what the launcher persists as a "known server".
    /// </summary>
    public string PortalUrl { get; set; } = string.Empty;

    /// <summary>Realmlist host written into the client's Config.wtf (what the game client dials).</summary>
    public string RealmlistHost { get; set; } = string.Empty;

    /// <summary>Realmlist/auth port (world address port).</summary>
    public int RealmlistPort { get; set; }

    /// <summary>Armory web port (0 when the stack has no armory).</summary>
    public int ArmoryPort { get; set; }

    /// <summary>Convenience absolute armory URL (blank when no armory).</summary>
    public string ArmoryUrl { get; set; } = string.Empty;

    public string Template { get; set; } = string.Empty;

    public string AccentColor { get; set; } = string.Empty;

    /// <summary>
    /// Relative path (resolved against <see cref="PortalUrl"/>) of this stack's effective launcher
    /// wallpaper, e.g. <c>/branding/background</c>. Blank when the stack serves no background. The image
    /// is the per-stack override when set, otherwise the global default, and is hosted by this stack's own
    /// client container so the launcher never contacts the manager for branding.
    /// </summary>
    public string BackgroundUrl { get; set; } = string.Empty;

    /// <summary>Relative path (resolved against <see cref="PortalUrl"/>) of this stack's effective logo,
    /// e.g. <c>/branding/logo</c>. Blank when the stack serves no logo.</summary>
    public string LogoUrl { get; set; } = string.Empty;

    /// <summary>
    /// Relative path (resolved against <see cref="PortalUrl"/>) of this stack's launcher news feed,
    /// e.g. <c>/news</c>. Blank when the stack has no news. The feed (and its cover images at
    /// <c>/news-image/{id}</c>) is hosted by this stack's own client container so the launcher never
    /// contacts the manager for news.
    /// </summary>
    public string NewsUrl { get; set; } = string.Empty;

    /// <summary>Informational WoW client version label shown by the launcher for this stack.</summary>
    public string ClientVersion { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>
    /// Monotonic per-entry revision (manager-assigned, e.g. unix-ms of the last change). Newer wins when
    /// the launcher reconciles differing copies of the same stack across the registry.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>Latest launcher version this stack can serve (informational; drives self-update source choice).</summary>
    public string LauncherVersion { get; set; } = string.Empty;
}

/// <summary>Metadata for the launcher executable a stack container serves for self-update.</summary>
public sealed class LauncherArtifactInfo
{
    public string Version { get; set; } = string.Empty;
    public DateTime? BuiltAt { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool DownloadAvailable { get; set; }
}

/// <summary>
/// The <c>build.json</c> the manager writes into a stack's <c>launcher-dist</c> volume alongside the
/// built launcher exe. The stack container reads it to answer <c>/launcher/latest</c> and to overlay
/// the launcher artifact info into its <c>/portal</c> document.
/// </summary>
public sealed class LauncherBuildManifest
{
    public string Version { get; set; } = string.Empty;

    /// <summary>File name of the exe within the launcher-dist volume (served by <c>/launcher/download</c>).</summary>
    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public DateTime BuiltAt { get; set; }
}

/// <summary>
/// The self-describing document each stack container serves at <c>GET /portal</c>. It carries this
/// stack's identity, global branding, the launcher artifact it hosts, and the full replicated registry
/// of all published stacks so the launcher keeps multi-stack functionality without the manager.
/// </summary>
public sealed class StackPortalDocument
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Monotonic revision of the whole registry snapshot the manager last pushed.</summary>
    public long RegistryRevision { get; set; }

    public DateTime GeneratedAt { get; set; }

    public string AppName { get; set; } = "Azeroth Platform";

    public string BrandingTitle { get; set; } = string.Empty;

    public string AccentColor { get; set; } = string.Empty;

    public string Template { get; set; } = string.Empty;

    /// <summary>Whether the launcher must require account login before play.</summary>
    public bool RequireLogin { get; set; }

    /// <summary>Base64 SPKI manifest signing public key (reference; the launcher prefers its baked copy).</summary>
    public string ManifestPublicKey { get; set; } = string.Empty;

    /// <summary>Which <see cref="Registry"/> entry is the stack serving this document.</summary>
    public string SelfStackId { get; set; } = string.Empty;

    /// <summary>The launcher build this stack hosts (overlaid by the container from its own dist volume).</summary>
    public LauncherArtifactInfo Launcher { get; set; } = new();

    /// <summary>Every published stack (including this one), for the launcher's multi-stack list.</summary>
    public List<StackRegistryEntry> Registry { get; set; } = new();
}
