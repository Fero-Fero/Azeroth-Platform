namespace AzerothPlatform.Launcher.Models;

/// <summary>Cached hash of a local file, keyed by relative path.</summary>
public sealed class HashCacheEntry
{
    public long Size { get; set; }
    public long MTimeTicks { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// Per-profile launcher state: which overlay files are stashed/active and which addons are
/// downloaded/enabled, so profile switching and addon toggling never re-download.
/// </summary>
public sealed class ProfileState
{
    /// <summary>Overlay manifest version last synced into this profile's stash.</summary>
    public string? LastOverlayVersion { get; set; }

    /// <summary>
    /// Overlay MPQ paths (relative to Data/) tracked for this profile. When active they live in
    /// <c>Data/</c>; when inactive they live in <c>Data/{FolderName}/</c>.
    /// </summary>
    public List<string> OverlayMpqs { get; set; } = new();

    /// <summary>Addon folder names downloaded into this profile's addon cache.</summary>
    public List<string> DownloadedAddons { get; set; } = new();

    /// <summary>Addon folder names the player has enabled for this profile.</summary>
    public List<string> EnabledAddons { get; set; } = new();

    /// <summary>
    /// Game-account name last successfully signed in for this profile, or null when signed out.
    /// Login is per-server, so this is remembered per profile: switching back to a server you've
    /// already authenticated with (or relaunching the launcher) keeps you signed in.
    /// </summary>
    public string? LoggedInUsername { get; set; }

    /// <summary>
    /// Verify token last acknowledged for this profile. When the server's manifest token differs, the
    /// launcher runs a one-off full verify (re-hash) of every base and overlay file, then records the
    /// new token here so the forced verify happens exactly once per operator request.
    /// </summary>
    public string? LastVerifyToken { get; set; }
}

/// <summary>
/// Persistent launcher state: connection settings, install location, last-synced manifest,
/// and a local hash cache so the full client is not rehashed on every launch.
/// </summary>
public sealed class LauncherState
{
    /// <summary>Base URL of the manager backend, e.g. "http://localhost:8080".</summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Persisted list of known stack portal URLs (e.g. "http://host:8101") the launcher reconciles the
    /// replicated registry from. Seeded from the baked portal URL on first run and grown as the launcher
    /// learns of other stacks from the registry or the player adds one manually. Self-healing: stale/
    /// unreachable entries are re-derived from the newest healthy stack on each reconcile.
    /// </summary>
    public List<string> KnownServers { get; set; } = new();

    /// <summary>
    /// Optional stack id. When set, the launcher targets that stack's per-stack client
    /// distribution (api/stacks/{stackId}/launcher/*) instead of the global client.
    /// </summary>
    public string? StackId { get; set; }

    /// <summary>Absolute path to the WoW client install directory.</summary>
    public string? InstallDirectory { get; set; }

    /// <summary>Manifest version last fully synced.</summary>
    public string? LastManifestVersion { get; set; }

    /// <summary>Managed file paths from the last sync, used to prune files removed server-side.</summary>
    public List<string> LastManagedPaths { get; set; } = new();

    /// <summary>Local file hash cache keyed by relative path.</summary>
    public Dictionary<string, HashCacheEntry> HashCache { get; set; } = new();

    // ===== Multi-profile state =====
    /// <summary>The profile (stack id) currently selected in the dropdown.</summary>
    public string? SelectedProfileId { get; set; }

    /// <summary>The profile whose overlay MPQs/addons are currently materialized into the install.</summary>
    public string? ActiveProfileId { get; set; }

    /// <summary>Per-profile stash/addon state, keyed by profile (stack) id.</summary>
    public Dictionary<string, ProfileState> Profiles { get; set; } = new();

    /// <summary>Version of the launcher binary that was last seen available on the server.</summary>
    public string? LastSeenLauncherVersion { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(InstallDirectory);

    public ProfileState GetProfile(string profileId)
    {
        if (!Profiles.TryGetValue(profileId, out var state))
        {
            state = new ProfileState();
            Profiles[profileId] = state;
        }

        return state;
    }
}
