namespace AzerothPlatform.Launcher.Models;

/// <summary>A settings file to write into the client install folder. Mirrors the backend contract.</summary>
public sealed class LauncherSettingsFile
{
    public string TargetRelativePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Overwrite { get; set; } = true;
}

/// <summary>Launcher configuration served by the backend.</summary>
public sealed class LauncherConfig
{
    public string GameExecutable { get; set; } = "Wow.exe";
    public string LaunchArguments { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = string.Empty;
    public string BrandingTitle { get; set; } = "Azeroth Platform Launcher";
    public string RealmlistHost { get; set; } = string.Empty;
    public int RealmlistPort { get; set; }
    public string ManifestVersion { get; set; } = string.Empty;

    /// <summary>
    /// Absolute base URL of this stack's client-server container (manifest + files). When set, the
    /// launcher fetches the single merged manifest and all files from here; when blank it falls back to
    /// the manager's legacy per-stack file endpoints.
    /// </summary>
    public string ClientContentBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Base64 SPKI ECDSA public key used to verify the client manifest's signature. Received over the
    /// trusted (TLS) manager channel. Empty when the server has signing disabled.
    /// </summary>
    public string ClientManifestPublicKey { get; set; } = string.Empty;

    public List<LauncherSettingsFile> Settings { get; set; } = new();
}
