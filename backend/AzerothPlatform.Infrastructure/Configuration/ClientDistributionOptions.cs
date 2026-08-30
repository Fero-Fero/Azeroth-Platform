namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Configuration for distributing the WoW client to the launcher.
/// Admins drop client files under <see cref="RootPath"/>; the backend generates a
/// manifest from them and serves them to the launcher.
/// </summary>
public sealed class ClientDistributionOptions
{
    public const string SectionName = "Client";

    /// <summary>
    /// Root directory that holds the distributable client.
    /// Expected layout:
    ///   {RootPath}/game/      -> files that map 1:1 into the player's WoW install folder
    ///   {RootPath}/settings/  -> realmlist.wtf.tmpl and Config.wtf.tmpl templates
    ///   {RootPath}/launcher.json (optional) -> overrides for the launcher config
    /// </summary>
    public string RootPath { get; set; } = "/app/data/client";

    /// <summary>
    /// Per-stack base client directory on the manager (<c>{RootPath}/stacks/{stackId}/game</c>). Each
    /// stack stores and serves its own base WoW client, so admins upload the client per stack.
    /// </summary>
    public string StackGameDir(string stackId) => Path.Combine(RootPath, "stacks", stackId, "game");

    /// <summary>
    /// Per-stack upload/extract staging under <see cref="RootPath"/> (the manager data volume), so
    /// local volume seeding can daemon-copy instead of tar-streaming through the Docker CLI.
    /// </summary>
    public string UploadStagingRoot(string stackId) => Path.Combine(RootPath, "upload-staging", stackId);

    /// <summary>
    /// Executable the launcher starts after syncing (relative to the install folder).
    /// </summary>
    public string GameExecutable { get; set; } = "Wow.exe";

    /// <summary>
    /// Extra arguments passed to the game executable on launch.
    /// </summary>
    public string LaunchArguments { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable client version shown in the launcher (informational).
    /// </summary>
    public string ClientVersion { get; set; } = "3.3.5a (12340)";

    /// <summary>
    /// Display name shown in the launcher window.
    /// </summary>
    public string BrandingTitle { get; set; } = "Azeroth Platform Launcher";

    /// <summary>
    /// Realmlist settings written into the client before launch.
    /// </summary>
    public RealmlistOptions Realmlist { get; set; } = new();

    /// <summary>
    /// Relative glob-like prefixes (under game/) whose files are treated as "managed":
    /// always hash-verified and kept in sync, and deleted locally when removed server-side.
    /// Everything else is treated as "base" (verified only on first install / full verify).
    /// </summary>
    public List<string> ManagedPrefixes { get; set; } = new() { "Data/patch-", "Interface/AddOns/" };
}

/// <summary>
/// Realmlist connection details substituted into realmlist.wtf.
/// </summary>
public sealed class RealmlistOptions
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 3724;
}
