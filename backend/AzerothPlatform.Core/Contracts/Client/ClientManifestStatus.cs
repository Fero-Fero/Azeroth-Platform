namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// What the client-server is currently serving to launchers. Read by the manager so the Client tab can
/// report the manifest players actually receive, which is not always the content sitting in the volume:
/// if a manifest refresh failed the two diverge, and only this side shows it.
///
/// Shared between the client-server (which produces it) and the manager (which parses the JSON), so the
/// property names stay in lockstep.
/// </summary>
public sealed class ClientManifestStatus
{
    /// <summary>Hash-of-hashes identifying the served content; changes whenever any file does.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Bumped by "force re-validate" to make launchers re-hash their local files.</summary>
    public string VerifyToken { get; set; } = string.Empty;

    public int FileCount { get; set; }

    /// <summary>Files served from the uploaded base client.</summary>
    public int BaseFileCount { get; set; }

    /// <summary>Files served from the per-stack overlay (published patches, server addons).</summary>
    public int ManagedFileCount { get; set; }

    public long TotalSize { get; set; }

    /// <summary>When the served manifest was last built, or null if it was never built this run.</summary>
    public DateTime? BuiltAtUtc { get; set; }

    /// <summary>False when no signing key is provisioned, meaning launchers cannot verify the manifest.</summary>
    public bool Signed { get; set; }
}
