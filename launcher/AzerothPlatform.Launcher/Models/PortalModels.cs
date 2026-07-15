namespace AzerothPlatform.Launcher.Models;

/// <summary>
/// A single stack's advertised connection info in the replicated registry. Mirrors the backend
/// <c>StackRegistryEntry</c> served at <c>GET /portal</c>.
/// </summary>
public sealed class StackRegistryEntry
{
    public string StackId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Absolute base URL of this stack's portal/client container.</summary>
    public string PortalUrl { get; set; } = string.Empty;

    public string RealmlistHost { get; set; } = string.Empty;
    public int RealmlistPort { get; set; }
    public int ArmoryPort { get; set; }
    public string ArmoryUrl { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string AccentColor { get; set; } = string.Empty;

    /// <summary>Relative path (against <see cref="PortalUrl"/>) of this stack's launcher wallpaper, or blank.</summary>
    public string BackgroundUrl { get; set; } = string.Empty;

    /// <summary>Relative path (against <see cref="PortalUrl"/>) of this stack's launcher logo, or blank.</summary>
    public string LogoUrl { get; set; } = string.Empty;

    /// <summary>Relative path (against <see cref="PortalUrl"/>) of this stack's launcher news feed, or blank.</summary>
    public string NewsUrl { get; set; } = string.Empty;

    /// <summary>Informational WoW client version label advertised by this stack.</summary>
    public string ClientVersion { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>Monotonic per-entry revision; newest wins when reconciling copies of the same stack.</summary>
    public long Revision { get; set; }

    public string LauncherVersion { get; set; } = string.Empty;
}

/// <summary>Metadata for the launcher exe a stack serves for self-update. Mirrors <c>LauncherArtifactInfo</c>.</summary>
public sealed class LauncherArtifactInfo
{
    public string Version { get; set; } = string.Empty;
    public DateTime? BuiltAt { get; set; }
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool DownloadAvailable { get; set; }
}

/// <summary>
/// The document a stack container serves at <c>GET /portal</c>: its identity, branding, the launcher it
/// hosts, and the full replicated registry. Mirrors the backend <c>StackPortalDocument</c>.
/// </summary>
public sealed class StackPortalDocument
{
    public int SchemaVersion { get; set; } = 1;
    public long RegistryRevision { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string AppName { get; set; } = "Azeroth Platform";
    public string BrandingTitle { get; set; } = string.Empty;
    public string AccentColor { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public bool RequireLogin { get; set; }
    public string ManifestPublicKey { get; set; } = string.Empty;
    public string SelfStackId { get; set; } = string.Empty;
    public LauncherArtifactInfo Launcher { get; set; } = new();
    public List<StackRegistryEntry> Registry { get; set; } = new();
}
