namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Describes how a manifest file should be treated by the launcher. The distinction is about
/// verification cost, not lifetime: files of either group are deleted from the player's install once
/// they stop appearing in the manifest.
/// </summary>
public enum ManifestFileGroup
{
    /// <summary>
    /// Large base client files. Downloaded when missing and only re-verified during a full verify.
    /// </summary>
    Base = 0,

    /// <summary>
    /// Custom/managed files (e.g. patch MPQs). Always hash-verified and kept in sync.
    /// </summary>
    Managed = 1
}

/// <summary>
/// A single downloadable file entry in the client manifest.
/// </summary>
public sealed class ManifestFile
{
    /// <summary>
    /// Path relative to the client install folder (forward-slash separated), e.g. "Data/patch-B.mpq".
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 hash of the file contents.
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// How the launcher should treat this file.
    /// </summary>
    public ManifestFileGroup Group { get; set; }
}

/// <summary>
/// A snapshot of the distributable client that the launcher syncs against.
/// </summary>
public sealed class ClientManifest
{
    /// <summary>
    /// Aggregate version hash derived from every file's path + hash. Changes whenever any file changes.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Opaque token an operator bumps to force launchers to full-verify (re-hash) every file on their
    /// next check, even when <see cref="Version"/> is unchanged (e.g. a same-size content edit).
    /// Empty when no forced verify has ever been requested.
    /// </summary>
    public string VerifyToken { get; set; } = string.Empty;

    /// <summary>
    /// When the manifest was generated (UTC).
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>
    /// Total size of all files in bytes.
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// All files that make up the client.
    /// </summary>
    public List<ManifestFile> Files { get; set; } = new();

    /// <summary>
    /// Base64 ECDSA (P-256/SHA-256) signature over the canonical manifest bytes (version, verify token,
    /// and every file's path/size/hash/group). Produced server-side with the platform's private signing
    /// key and verified by the launcher against the public key it receives over the trusted (TLS) config
    /// channel, so a MITM cannot swap files+hashes even when file content is served over plain HTTP.
    /// Empty when the server has signing disabled (older/unconfigured deployments).
    /// </summary>
    public string Signature { get; set; } = string.Empty;
}
