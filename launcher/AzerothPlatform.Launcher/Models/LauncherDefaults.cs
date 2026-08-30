namespace AzerothPlatform.Launcher.Models;

/// <summary>
/// Optional defaults shipped next to the launcher executable as <c>launcher.settings.json</c>.
/// Lets whoever distributes the launcher pre-set the download URL and branding so friends can
/// install and play with minimal configuration.
/// </summary>
public sealed class LauncherDefaults
{
    /// <summary>Pre-configured backend / client download URL.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Optional stack id to distribute a specific stack's client. When set, the launcher targets
    /// api/stacks/{stackId}/launcher/* so friends download that stack's patched client.
    /// </summary>
    public string? StackId { get; set; }

    /// <summary>Window/branding title shown before the server is contacted.</summary>
    public string? BrandingTitle { get; set; }

    /// <summary>
    /// App/install name baked at compile time. When set, the launcher installs the shared client to
    /// <c>%LOCALAPPDATA%/{AppName}</c> and fetches multi-profile data from the backend.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// When true, the launcher runs in multi-profile mode: it fetches selectable server profiles from
    /// the backend and swaps per-profile MPQs/addons over one shared client install.
    /// </summary>
    public bool MultiProfile { get; set; }

    /// <summary>
    /// When true, the launcher shows a login screen and requires the player to authenticate with a
    /// game account before they can download or play. Baked at compile time (changing it on the
    /// website requires a new build).
    /// </summary>
    public bool RequireLogin { get; set; }

    /// <summary>Build version baked at compile time, used for self-update checks.</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Base64 SPKI ECDSA manifest signing public key baked at compile time. When set, the launcher runs
    /// in "stack portal" mode: it talks to the stack container's <c>/portal</c> + reconciles the
    /// replicated registry, and verifies client manifests against this baked key. Config arrives over the
    /// stack's plain HTTP, so this key is the only anchor for manifest trust.
    /// </summary>
    public string? SigningPublicKey { get; set; }

    /// <summary>
    /// Suggested install folder name. Placed under the per-user local-app-data folder
    /// (<c>%LOCALAPPDATA%</c> on Windows). Defaults to "Azeroth Platform".
    /// </summary>
    public string? DefaultInstallSubfolder { get; set; }

    /// <summary>
    /// Optional absolute install path override. When set it takes precedence over
    /// <see cref="DefaultInstallSubfolder"/> (e.g. <c>C:\Games\MyRealm</c>).
    /// </summary>
    public string? DefaultInstallDirectory { get; set; }
}
