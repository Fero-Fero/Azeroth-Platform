namespace AzerothPlatform.Launcher.Models;

/// <summary>
/// How the launcher treats a manifest file. Mirrors the backend contract. The distinction is about
/// verification cost, not lifetime: files of either group are deleted locally once the server stops
/// listing them.
/// </summary>
public enum ManifestFileGroup
{
    /// <summary>Large base client file; downloaded when missing, re-verified only on full verify.</summary>
    Base = 0,

    /// <summary>Managed file (patches/config); always hash-verified.</summary>
    Managed = 1
}

/// <summary>A single downloadable file entry.</summary>
public sealed class ManifestFile
{
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public ManifestFileGroup Group { get; set; }
}

/// <summary>A snapshot of the distributable client.</summary>
public sealed class ClientManifest
{
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Operator-controlled token. When it changes the launcher full-verifies (re-hashes) every file on
    /// its next check, even if <see cref="Version"/> is unchanged. Empty when never forced.
    /// </summary>
    public string VerifyToken { get; set; } = string.Empty;

    public DateTime GeneratedAt { get; set; }
    public long TotalSize { get; set; }
    public List<ManifestFile> Files { get; set; } = new();

    /// <summary>
    /// Base64 ECDSA (P-256/SHA-256) signature over the canonical manifest bytes. Verified against the
    /// public key delivered by the manager over TLS before any file/hash is trusted. Empty when the
    /// server has signing disabled.
    /// </summary>
    public string Signature { get; set; } = string.Empty;
}
