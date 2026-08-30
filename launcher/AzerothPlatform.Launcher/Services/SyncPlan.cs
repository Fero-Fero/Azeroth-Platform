using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>The set of changes required to bring the local install in line with the manifest.</summary>
public sealed class SyncPlan
{
    /// <summary>Files that must be (re)downloaded.</summary>
    public List<ManifestFile> Downloads { get; } = new();

    /// <summary>Absolute paths of files to delete because the server no longer lists them.</summary>
    public List<string> Deletions { get; } = new();

    /// <summary>All managed relative paths in the current manifest (persisted for future pruning).</summary>
    public List<string> ManagedPaths { get; } = new();

    /// <summary>All base relative paths in the current manifest (persisted for future pruning).</summary>
    public List<string> BasePaths { get; } = new();

    /// <summary>
    /// How many stale files were left in place because the removal set looked implausibly large. Non-zero
    /// means the install keeps files the server no longer lists, which the player should be told about.
    /// </summary>
    public int RefusedRemovals { get; set; }

    public long BytesToDownload => Downloads.Sum(f => f.Size);

    public bool IsUpToDate => Downloads.Count == 0 && Deletions.Count == 0;
}

/// <summary>
/// Which previously synced files the current manifest no longer lists. When the set is implausibly
/// large it is dropped wholesale and reported as <see cref="RefusedCount"/> instead: leaving stale
/// files behind is recoverable, deleting a player's install is not.
/// </summary>
public readonly record struct RemovalPlan(IReadOnlyList<string> Paths, int RefusedCount)
{
    public static RemovalPlan Remove(IReadOnlyList<string> paths) => new(paths, 0);

    public static RemovalPlan Refuse(int count) => new([], count);
}

/// <summary>
/// Relative paths the previous successful sync recorded, split by group. Anything listed here that the
/// current manifest no longer contains is a file the server dropped, so the launcher removes its local
/// copy. Tracking base paths as well as managed ones is what lets a file deleted from the client
/// actually disappear from players' installs.
/// </summary>
public readonly record struct PreviouslySyncedPaths(
    IReadOnlyCollection<string> Managed,
    IReadOnlyCollection<string> Base)
{
    public static PreviouslySyncedPaths None => new([], []);
}
