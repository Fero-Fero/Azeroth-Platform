namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Summary of the shared base WoW client the admin has uploaded (the seed that every stack's client
/// container serves as its read-only base layer). Surfaced on the global Client admin tab.
/// </summary>
public sealed class ClientBaseInfoDto
{
    /// <summary>True when a base client has been uploaded (Wow.exe, Data MPQs, or other client files present).</summary>
    public bool Exists { get; set; }

    /// <summary>True when the stack's <c>client-base</c> Docker volume exists on the engine (even if unreadable).</summary>
    public bool VolumeExists { get; set; }

    /// <summary>
    /// Set when the volume exists but the manager could not inspect it (e.g. remote Docker unreachable).
    /// </summary>
    public string? InspectionWarning { get; set; }

    /// <summary>Number of files in the base client.</summary>
    public int FileCount { get; set; }

    /// <summary>Total size of the base client in bytes.</summary>
    public long TotalSize { get; set; }

    /// <summary>True when <c>Wow.exe</c> is present at the base root (a basic sanity check).</summary>
    public bool HasWowExe { get; set; }

    /// <summary>True when at least one <c>Data/*.MPQ</c> exists (the client's data archives).</summary>
    public bool HasDataMpq { get; set; }

    /// <summary>Absolute path of the base client's <c>game/</c> directory on the manager host.</summary>
    public string GamePath { get; set; } = string.Empty;

    /// <summary>True when a configured base-client download URL is available.</summary>
    public bool DownloadAvailable { get; set; }

    /// <summary>Shown when <see cref="DownloadAvailable"/> is false.</summary>
    public string? DownloadUnavailableReason { get; set; }

    /// <summary>
    /// Set when the content change succeeded but the launcher manifest could not be refreshed straight
    /// away (client container stopped, engine unreachable). The change is not lost — the client-server
    /// re-checks its content on a timer — but propagation to launchers is delayed, so say so rather
    /// than reporting an unqualified success.
    /// </summary>
    public string? ManifestWarning { get; set; }

    /// <summary>
    /// What the stack's client-server is currently serving to launchers. Null when the stack has no
    /// client container or it could not be reached. Shown next to the volume figures above because the
    /// two disagreeing is the symptom operators need to see: files are in the volume but players are
    /// still being offered the previous manifest.
    /// </summary>
    public ClientManifestStatus? Manifest { get; set; }
}
