namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Configuration for the stack migration/patch system.
/// </summary>
public sealed class MigrationOptions
{
    public const string SectionName = "Migrations";

    /// <summary>
    /// Docker image (built from <c>wdbx/Dockerfile</c>) used to import CSV into DBC files via Wine.
    /// </summary>
    public string WdbxImage { get; set; } = "azerothcore-wdbx:latest";

    /// <summary>
    /// Lightweight image (built from <c>mpqtool/Dockerfile</c>) used to pack compiled DBC files into a
    /// client MPQ (patch-D). Kept separate from the heavy WDBX/Wine image so it builds fast and touching
    /// MPQ tooling never rebuilds WDBX.
    /// </summary>
    public string MpqToolImage { get; set; } = "azerothcore-mpqtool:latest";

    /// <summary>
    /// File name of the client MPQ generated from the compiled DBC files. Matches the launcher's
    /// <c>Data/patch-</c> managed prefix so players are auto-prompted to update when it changes.
    /// </summary>
    public string PatchDMpqName { get; set; } = "patch-D.MPQ";

    /// <summary>Path to the MPQ sidecar source baked into the manager image (Dockerfile COPY mpqtool/).</summary>
    public string MpqToolSourcePath { get; set; } = "/app/mpqtool-src";

    /// <summary>Writable working dir the MPQ source is copied into before build (host-visible context).</summary>
    public string MpqToolWorkPath { get; set; } = "/app/data/mpqtool-build";

    /// <summary>Path to the WDBX sidecar source baked into the manager image (Dockerfile COPY wdbx/).</summary>
    public string WdbxSourcePath { get; set; } = "/app/wdbx-src";

    /// <summary>Writable working dir the WDBX source is copied into before build (host-visible context).</summary>
    public string WdbxWorkPath { get; set; } = "/app/data/wdbx-build";

    /// <summary>
    /// Lightweight image used to read/write the stack's data volume (dbc/maps) via bind mounts.
    /// </summary>
    public string VolumeToolImage { get; set; } = "alpine:3.20";

    /// <summary>
    /// WoW client build number passed to WDBXEditor when loading DBC files (3.3.5a = 12340).
    /// </summary>
    public int WoWBuild { get; set; } = 12340;

    /// <summary>
    /// Realmlist host advertised to launchers for per-stack client distribution.
    /// </summary>
    public string RealmlistHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// Scheme the launcher should use to reach a stack's client file server (<c>ClientContentBaseUrl</c>).
    /// <c>auto</c> (default) picks <c>http</c> for loopback / private-LAN hosts (where the client port is
    /// not internet-reachable and has no TLS of its own) and <c>https</c> for public hosts, so an
    /// internet-facing deployment never advertises a plaintext client URL (front the client port with a
    /// TLS terminator). Force a specific scheme with <c>http</c> or <c>https</c>.
    /// </summary>
    public string ClientContentScheme { get; set; } = "auto";

    /// <summary>
    /// Source directory of client settings templates (realmlist.wtf.tmpl, Config.wtf.tmpl) baked into
    /// the manager image. Copied into each stack's <c>client/settings/</c> on scaffold so the launcher
    /// always receives a realmlist.wtf to write.
    /// </summary>
    public string ClientSettingsTemplatePath { get; set; } = "/app/client-example/settings";

    /// <summary>
    /// Optional path to a local clone of Azeroth-Platform-Progression used for patch structure validation.
    /// When unset, validation looks for a sibling directory named <c>Azeroth-Platform-Progression</c>
    /// next to the platform repository root.
    /// </summary>
    public string? ProgressionRepoPath { get; set; }
}
