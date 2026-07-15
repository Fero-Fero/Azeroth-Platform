using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>The set of changes required to bring the local install in line with the manifest.</summary>
public sealed class SyncPlan
{
    /// <summary>Files that must be (re)downloaded.</summary>
    public List<ManifestFile> Downloads { get; } = new();

    /// <summary>Absolute paths of managed files to delete (removed server-side).</summary>
    public List<string> Deletions { get; } = new();

    /// <summary>All managed relative paths in the current manifest (persisted for future pruning).</summary>
    public List<string> ManagedPaths { get; } = new();

    public long BytesToDownload => Downloads.Sum(f => f.Size);

    public bool IsUpToDate => Downloads.Count == 0 && Deletions.Count == 0;
}
