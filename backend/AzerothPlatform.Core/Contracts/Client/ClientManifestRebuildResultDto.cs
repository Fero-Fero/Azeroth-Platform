namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Result of rebuilding a stack's client manifest: fresh hashes, corrected file groups, and a bumped
/// verify token so launchers re-sync on their next check.
/// </summary>
public sealed class ClientManifestRebuildResultDto
{
    public string Version { get; set; } = string.Empty;
    public string VerifyToken { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
    public int BaseFileCount { get; set; }
    public long BaseTotalSize { get; set; }
    public int ManagedFileCount { get; set; }
    public long ManagedTotalSize { get; set; }
}
