namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// A settings file that the launcher writes into the client install folder before launch.
/// Rendered server-side (realmlist host/port already substituted).
/// </summary>
public sealed class LauncherSettingsFileDto
{
    /// <summary>
    /// Destination path relative to the install folder (e.g. "Data/enUS/realmlist.wtf").
    /// </summary>
    public string TargetRelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Final file contents to write.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// When true, the launcher always overwrites this file on every launch.
    /// When false, it only writes the file if it does not already exist.
    /// </summary>
    public bool Overwrite { get; set; } = true;
}

/// <summary>
/// Launcher configuration served to clients: how to launch the game, branding,
/// realmlist details, and the pre-defined settings files to apply.
/// </summary>
public sealed class LauncherConfigDto
{
    /// <summary>
    /// Executable to start after syncing (relative to the install folder).
    /// </summary>
    public string GameExecutable { get; set; } = "Wow.exe";

    /// <summary>
    /// Extra arguments passed to the game executable.
    /// </summary>
    public string LaunchArguments { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable client version (informational).
    /// </summary>
    public string ClientVersion { get; set; } = string.Empty;

    /// <summary>
    /// Display name shown in the launcher.
    /// </summary>
    public string BrandingTitle { get; set; } = "Azeroth Platform Launcher";

    /// <summary>
    /// Realmlist host players connect to.
    /// </summary>
    public string RealmlistHost { get; set; } = string.Empty;

    /// <summary>
    /// Realmlist port players connect to.
    /// </summary>
    public int RealmlistPort { get; set; }

    /// <summary>
    /// Current manifest version, so the launcher can quick-check for updates without a full diff.
    /// </summary>
    public string ManifestVersion { get; set; } = string.Empty;

    /// <summary>
    /// Absolute base URL of this stack's self-contained client-server container (e.g.
    /// <c>http://play.example:8123</c>). The launcher fetches the merged manifest (<c>/manifest</c>)
    /// and files (<c>/files/{path}</c>) from here instead of the manager. Blank falls back to the
    /// manager's per-stack file endpoints (legacy).
    /// </summary>
    public string ClientContentBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Base64 SPKI ECDSA public key the launcher uses to verify the client manifest's signature. Served
    /// over the trusted (TLS) manager channel so the launcher can validate manifests even when files are
    /// fetched over plain HTTP from a separate client-server. Empty when the server has signing disabled.
    /// </summary>
    public string ClientManifestPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Pre-defined settings files (realmlist.wtf, Config.wtf, etc.) rendered and ready to write.
    /// </summary>
    public List<LauncherSettingsFileDto> Settings { get; set; } = new();
}
