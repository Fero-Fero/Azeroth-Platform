using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Diffs the server manifest against the local install and applies the required downloads and
/// deletions, with HTTP-range resume, bounded parallelism, and aggregate progress reporting.
/// </summary>
public sealed class SyncService
{
    private const int MaxParallelDownloads = 4;

    private readonly ManifestClient _client;
    private readonly HashService _hashService;

    public SyncService(ManifestClient client, HashService hashService)
    {
        _client = client;
        _hashService = hashService;
    }

    /// <summary>
    /// Builds a plan describing what to download/delete. When <paramref name="fullVerify"/> is true,
    /// base files are hash-verified too (otherwise only their presence and size are checked).
    /// </summary>
    public async Task<SyncPlan> PlanAsync(
        ClientManifest manifest,
        string installDirectory,
        IReadOnlyCollection<string> previousManagedPaths,
        bool fullVerify,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = new SyncPlan();
        var total = manifest.Files.Count;
        var index = 0;

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            if (file.Group == ManifestFileGroup.Managed)
            {
                plan.ManagedPaths.Add(file.RelativePath);
            }

            var localPath = ToLocalPath(installDirectory, file.RelativePath);
            var needsDownload = await NeedsDownloadAsync(file, localPath, fullVerify, cancellationToken);
            if (needsDownload)
            {
                plan.Downloads.Add(file);
            }

            progress?.Report(new SyncProgress
            {
                // Only the full-verify pass actually hashes files; the quick pass just checks presence
                // and size, so label it "Checking" to avoid implying a hash verification is running.
                Status = fullVerify
                    ? $"Verifying files ({index}/{total})"
                    : $"Checking files ({index}/{total})",
                FilesCompleted = index,
                FilesTotal = total,
                Fraction = total == 0 ? 1 : (double)index / total
            });
        }

        var currentManaged = new HashSet<string>(plan.ManagedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var previous in previousManagedPaths)
        {
            if (currentManaged.Contains(previous))
            {
                continue;
            }

            var localPath = ToLocalPath(installDirectory, previous);
            if (File.Exists(localPath))
            {
                plan.Deletions.Add(localPath);
            }
        }

        return plan;
    }

    /// <summary>Applies the plan: deletes removed files, then downloads (in parallel) and verifies.</summary>
    public async Task ExecuteAsync(
        SyncPlan plan,
        string installDirectory,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var path in plan.Deletions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(path);
        }

        var totalBytes = plan.BytesToDownload;
        var totalFiles = plan.Downloads.Count;
        long bytesCompleted = 0;
        var filesCompleted = 0;

        void ReportOverall()
        {
            if (totalFiles == 0)
            {
                return;
            }

            progress?.Report(new SyncProgress
            {
                Status = $"Downloading {filesCompleted}/{totalFiles} files",
                FilesCompleted = filesCompleted,
                FilesTotal = totalFiles,
                BytesCompleted = Interlocked.Read(ref bytesCompleted),
                BytesTotal = totalBytes,
                Fraction = totalBytes == 0 ? 1 : (double)Interlocked.Read(ref bytesCompleted) / totalBytes
            });
        }

        ReportOverall();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxParallelDownloads,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(plan.Downloads, options, async (file, token) =>
        {
            var localPath = ToLocalPath(installDirectory, file.RelativePath);
            long lastReported = 0;

            var fileProgress = new Progress<long>(current =>
            {
                var delta = current - lastReported;
                lastReported = current;
                if (delta != 0)
                {
                    Interlocked.Add(ref bytesCompleted, delta);
                    ReportOverall();
                }
            });

            await _client.DownloadFileAsync(file.RelativePath, localPath, file.Size, fileProgress, token);

            var actualHash = await _hashService.GetHashAsync(file.RelativePath, localPath, forceRecompute: true, token);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Downloaded file failed verification: {file.RelativePath}");
            }

            _hashService.UpdateCache(file.RelativePath, localPath, file.Sha256);
            Interlocked.Increment(ref filesCompleted);
            ReportOverall();
        });

        if (totalFiles > 0)
        {
            progress?.Report(new SyncProgress
            {
                Status = "Up to date",
                FilesCompleted = totalFiles,
                FilesTotal = totalFiles,
                BytesCompleted = totalBytes,
                BytesTotal = totalBytes,
                Fraction = 1
            });
        }
    }

    /// <summary>
    /// Makes the install directory mirror the server: deletes every file whose relative path is not in
    /// <paramref name="serverRelativePaths"/> (the full base+overlay manifest) and does not live under a
    /// protected directory (per-profile stashes/addon caches and user/runtime folders such as WTF), then
    /// removes any directories left empty. Returns the deleted file paths. Used by the "invalidate cache
    /// &amp; re-verify" action to purge stale/unrecognised content while preserving profile and user data.
    /// </summary>
    public IReadOnlyList<string> PruneToServerManifest(
        string installDirectory,
        IEnumerable<string> serverRelativePaths,
        IEnumerable<string> protectedRelativeDirs,
        CancellationToken cancellationToken,
        double maxDeleteFraction = 0.5)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(installDirectory))
        {
            return deleted;
        }

        var allowed = new HashSet<string>(serverRelativePaths.Select(NormalizeRelative), StringComparer.OrdinalIgnoreCase);
        var protectedPrefixes = protectedRelativeDirs
            .Select(NormalizeRelative)
            .Where(d => d.Length > 0)
            .Select(d => d + "/")
            .ToList();

        bool IsProtected(string relWithSlash) =>
            protectedPrefixes.Any(p => relWithSlash.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        // Collect deletion candidates first so we can apply a safety threshold before touching disk: an
        // (unexpected) manifest that would wipe most of the install is refused rather than executed. This
        // is defense in depth on top of the manifest signature check performed before prune is called.
        var candidates = new List<string>();
        var totalConsidered = 0;
        foreach (var file in Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = NormalizeRelative(Path.GetRelativePath(installDirectory, file));
            if (IsProtected(rel + "/"))
            {
                continue;
            }

            totalConsidered++;
            if (!allowed.Contains(rel))
            {
                candidates.Add(file);
            }
        }

        // Guard against a catastrophic wipe: if we'd delete more than the allowed fraction of the
        // (non-protected) install AND more than a small absolute floor, skip pruning entirely.
        const int absoluteFloor = 50;
        if (candidates.Count > absoluteFloor
            && totalConsidered > 0
            && (double)candidates.Count / totalConsidered > maxDeleteFraction)
        {
            return deleted;
        }

        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(file);
            deleted.Add(file);
        }

        // Remove now-empty directories, deepest first, but never a protected subtree or the root.
        foreach (var dir in Directory.EnumerateDirectories(installDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = NormalizeRelative(Path.GetRelativePath(installDirectory, dir));
            if (IsProtected(rel + "/"))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch
            {
                // Best-effort; a locked/in-use directory is left in place.
            }
        }

        return deleted;
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    private async Task<bool> NeedsDownloadAsync(
        ManifestFile file, string localPath, bool fullVerify, CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
        {
            return true;
        }

        var info = new FileInfo(localPath);
        if (info.Length != file.Size)
        {
            return true;
        }

        // Base files: trust presence + size unless a full verify was requested.
        if (file.Group == ManifestFileGroup.Base && !fullVerify)
        {
            return false;
        }

        // A full verify re-hashes from disk (ignoring the cache) so corruption with an unchanged
        // size/mtime is still detected and the file is re-downloaded.
        var hash = await _hashService.GetHashAsync(file.RelativePath, localPath, fullVerify, cancellationToken);
        return !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a server-provided manifest relative path to an absolute local path and guarantees the
    /// result stays under <paramref name="installDirectory"/>. Rejects absolute paths, drive letters and
    /// <c>..</c> traversal so a malicious/compromised manifest cannot make the launcher write outside the
    /// install folder (defense in depth on top of manifest signature verification).
    /// </summary>
    internal static string ToLocalPath(string installDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Manifest contained an empty file path.");
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || (normalized.Length >= 2 && normalized[1] == ':')
            || normalized.Split('/').Any(s => s == ".."))
        {
            throw new InvalidOperationException($"Manifest contained an unsafe file path: {relativePath}");
        }

        var rootFull = Path.GetFullPath(installDirectory);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));

        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Manifest file path escapes the install directory: {relativePath}");
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort prune; ignore locked/missing files.
        }
    }
}
