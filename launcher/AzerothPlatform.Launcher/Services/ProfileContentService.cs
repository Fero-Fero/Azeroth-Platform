using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Manages per-profile overlay content over one shared WoW install: downloads each profile's custom
/// MPQs into a stash (<c>Data/{profile}/</c>) and addons into a cache (<c>_acl/addons/{profile}/</c>),
/// then swaps them in/out of the live install on profile switch — without ever re-downloading and
/// without touching the shared standard MPQs.
/// </summary>
public sealed class ProfileContentService
{
    private const string AddonCacheRoot = "_acl";

    public string DataDir(string install) => Path.Combine(install, "Data");
    public string StashDir(string install, LauncherProfile profile) => Path.Combine(DataDir(install), profile.FolderName);
    public string AddonCacheDir(string install, LauncherProfile profile) =>
        Path.Combine(install, AddonCacheRoot, "addons", profile.FolderName);
    public string LiveAddonsDir(string install) => Path.Combine(install, "Interface", "AddOns");
    public string CacheDir(string install) => Path.Combine(install, "Cache");

    /// <summary>
    /// Deletes the WoW client <c>Cache/</c> folder. The game rebuilds it on launch; clearing it before
    /// every start avoids stale MPQ indexing when overlay patches change.
    /// </summary>
    public void ClearClientCache(string install) => TryDeleteDirectory(CacheDir(install));

    /// <summary>
    /// Moves standard Blizzard MPQs out of a profile stash back into live <c>Data/</c>. Older launcher
    /// versions incorrectly stashed these per profile, which caused duplicate archives and stale MPQ
    /// indexing in tools and the game client.
    /// </summary>
    public void RestoreMisplacedSharedBaseMpqs(string install, LauncherProfile profile)
    {
        var stash = StashDir(install, profile);
        if (!Directory.Exists(stash))
        {
            return;
        }

        var dataDir = DataDir(install);
        foreach (var fileName in Directory.EnumerateFiles(stash))
        {
            var name = Path.GetFileName(fileName);
            if (!SharedClientDataFiles.IsSharedBaseDataFile($"Data/{name}"))
            {
                continue;
            }

            var live = Path.Combine(dataDir, name);
            if (File.Exists(live))
            {
                TryDeleteFile(fileName);
            }
            else
            {
                MoveFile(fileName, live);
            }
        }
    }

    /// <summary>
    /// Removes stale duplicate overlay MPQs that exist in both <c>Data/</c> (live) and the profile stash
    /// (<c>Data/{stackId}/</c>). Manual edits to live files often leave an older copy in the stash; MPQ
    /// tools that scan subfolders can then open the empty stash file while the game reads another path.
    /// When both copies exist with different sizes, keep the larger archive and delete the other.
    /// </summary>
    public void ReconcileOverlayDuplicates(
        string install,
        LauncherProfile profile,
        IReadOnlyList<string> overlayMpqs,
        bool profileIsActive = false)
    {
        var dataDir = DataDir(install);
        var stash = StashDir(install, profile);
        foreach (var mpq in overlayMpqs)
        {
            if (!SharedClientDataFiles.IsProfileOverlayMpq(mpq))
            {
                continue;
            }

            var live = Path.Combine(dataDir, mpq.Replace('/', Path.DirectorySeparatorChar));
            var stashed = Path.Combine(stash, mpq.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(live) || !File.Exists(stashed))
            {
                continue;
            }

            var liveLen = new FileInfo(live).Length;
            var stashLen = new FileInfo(stashed).Length;
            if (liveLen == stashLen)
            {
                // When active, live Data/ is canonical. Before activation, the stash copy is.
                TryDeleteFile(profileIsActive ? stashed : live);
                continue;
            }

            if (liveLen > stashLen)
            {
                TryDeleteFile(stashed);
            }
            else
            {
                TryDeleteFile(live);
            }
        }
    }

    /// <summary>
    /// Counts overlay manifest files that must be (re)downloaded into this profile's stash — missing,
    /// wrong size, or (when <paramref name="forceRecompute"/> is set) hash mismatch on disk.
    /// </summary>
    public async Task<int> CountOverlayDownloadsNeededAsync(
        ClientManifest overlayManifest,
        string install,
        LauncherProfile profile,
        HashService hashService,
        bool forceRecompute,
        CancellationToken cancellationToken)
    {
        var stash = StashDir(install, profile);
        var addonCache = AddonCacheDir(install, profile);
        var pending = 0;

        foreach (var file in overlayManifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = file.RelativePath.Replace('\\', '/');

            string dest;
            string? livePath = null;
            string cacheKey;

            if (rel.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            {
                var underData = rel.Substring("Data/".Length);
                if (!SharedClientDataFiles.IsProfileOverlayMpq(underData))
                {
                    continue;
                }

                dest = SafeCombine(stash, underData);
                livePath = Path.Combine(DataDir(install), underData.Replace('/', Path.DirectorySeparatorChar));
                cacheKey = $"overlay/{profile.FolderName}/{underData}";
            }
            else if (rel.StartsWith("Interface/AddOns/", StringComparison.OrdinalIgnoreCase))
            {
                var underAddons = rel.Substring("Interface/AddOns/".Length);
                dest = SafeCombine(addonCache, underAddons);
                cacheKey = $"overlay-addon/{profile.FolderName}/{underAddons}";
            }
            else
            {
                continue;
            }

            var needs = await NeedsDownloadAsync(file, dest, cacheKey, hashService, forceRecompute, cancellationToken);
            if (needs && livePath is not null && File.Exists(livePath))
            {
                var liveHash = await hashService.GetHashAsync(
                    $"{cacheKey}:live",
                    livePath,
                    forceRecompute,
                    cancellationToken);
                if (string.Equals(liveHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    needs = false;
                }
            }

            if (needs)
            {
                pending++;
            }
        }

        return pending;
    }

    /// <summary>
    /// True when this profile's stash/addon cache is out of date relative to the overlay manifest, or
    /// when overlay Data files are missing locally (e.g. after a prior sync skipped patch-2.MPQ because
    /// it matched a standard Blizzard archive name).
    /// </summary>
    public bool NeedsSync(
        ProfileState state,
        ClientManifest overlayManifest,
        string install,
        LauncherProfile profile,
        string? activeProfileId)
    {
        if (!string.Equals(state.LastOverlayVersion, overlayManifest.Version, StringComparison.Ordinal))
        {
            return true;
        }

        if (CountMissingOverlayDataFiles(install, profile, overlayManifest, activeProfileId) > 0)
        {
            return true;
        }

        // Retired overlay MPQs can linger in live Data/ when the server drops them but the overlay
        // version string is unchanged, or when Update skipped overlay sync.
        return CountRetiredLiveOverlayMpqs(install, overlayManifest) > 0;
    }

    /// <summary>
    /// Overlay Data files absent from this profile's stash (or live Data/ when active).
    /// </summary>
    public int CountMissingOverlayDataFiles(
        string install,
        LauncherProfile profile,
        ClientManifest overlayManifest,
        string? activeProfileId)
    {
        var missing = 0;
        var isActive = string.Equals(activeProfileId, profile.StackId, StringComparison.Ordinal);
        foreach (var file in overlayManifest.Files)
        {
            if (!IsOverlayDataPath(file.RelativePath, out var underData)
                || !SharedClientDataFiles.IsProfileOverlayMpq(underData))
            {
                continue;
            }

            if (!OverlayDataFilePresent(install, profile, underData, file.Size, isActive))
            {
                missing++;
            }
        }

        return missing;
    }

    /// <summary>
    /// Profile overlay MPQs still sitting in live <c>Data/</c> but no longer listed in the overlay manifest.
    /// WoW loads every MPQ in that folder, so these must be removed when a patch is retired server-side.
    /// </summary>
    public int CountRetiredLiveOverlayMpqs(string install, ClientManifest overlayManifest)
    {
        var keep = CollectOverlayMpqNames(overlayManifest);
        var dataDir = DataDir(install);
        if (!Directory.Exists(dataDir))
        {
            return 0;
        }

        var retired = 0;
        foreach (var file in Directory.EnumerateFiles(dataDir))
        {
            var name = Path.GetFileName(file);
            if (SharedClientDataFiles.IsProfileOverlayMpq(name) && !keep.Contains(name))
            {
                retired++;
            }
        }

        return retired;
    }

    /// <summary>
    /// Deletes profile overlay MPQs from live <c>Data/</c> that the server no longer publishes.
    /// </summary>
    public void PruneRetiredLiveOverlayMpqs(string install, ClientManifest overlayManifest) =>
        PruneLiveOverlayMpqs(install, CollectOverlayMpqNames(overlayManifest));

    /// <summary>
    /// Drops retired overlay MPQs from live <c>Data/</c> and refreshes the profile's overlay MPQ list
    /// from the server manifest without re-downloading.
    /// </summary>
    public void ReconcileOverlayState(string install, ClientManifest overlayManifest, ProfileState state)
    {
        PruneRetiredLiveOverlayMpqs(install, overlayManifest);
        state.OverlayMpqs = CollectOverlayMpqNames(overlayManifest).ToList();
    }

    private static HashSet<string> CollectOverlayMpqNames(ClientManifest overlayManifest)
    {
        var mpqs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in overlayManifest.Files)
        {
            if (IsOverlayDataPath(file.RelativePath, out var underData)
                && SharedClientDataFiles.IsProfileOverlayMpq(underData))
            {
                mpqs.Add(underData);
            }
        }

        return mpqs;
    }

    private static void PruneLiveOverlayMpqs(string install, IEnumerable<string> keepUnderData)
    {
        var keep = new HashSet<string>(keepUnderData, StringComparer.OrdinalIgnoreCase);
        var dataDir = Path.Combine(install, "Data");
        if (!Directory.Exists(dataDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dataDir))
        {
            var name = Path.GetFileName(file);
            if (SharedClientDataFiles.IsProfileOverlayMpq(name) && !keep.Contains(name))
            {
                TryDeleteFile(file);
            }
        }
    }

    private bool OverlayDataFilePresent(
        string install,
        LauncherProfile profile,
        string underData,
        long expectedSize,
        bool isActive)
    {
        var dataDir = DataDir(install);
        var stash = StashDir(install, profile);
        var live = Path.Combine(dataDir, underData.Replace('/', Path.DirectorySeparatorChar));
        var stashed = Path.Combine(stash, underData.Replace('/', Path.DirectorySeparatorChar));

        if (FileMatches(live, expectedSize))
        {
            return true;
        }

        // When active, files normally live in Data/; when inactive, in the stash. Check both so a
        // half-applied switch or a stale marker still triggers a re-download.
        return FileMatches(stashed, expectedSize);
    }

    private static bool FileMatches(string path, long expectedSize) =>
        File.Exists(path) && new FileInfo(path).Length == expectedSize;

    private static bool IsOverlayDataPath(string relativePath, out string underData)
    {
        var rel = relativePath.Replace('\\', '/');
        if (rel.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            underData = rel.Substring("Data/".Length);
            return true;
        }

        underData = string.Empty;
        return false;
    }

    /// <summary>
    /// Downloads this profile's overlay content into its stash + addon cache and prunes anything
    /// removed server-side. Never writes into the live install root.
    /// </summary>
    public async Task SyncOverlayAsync(
        ManifestClient overlayClient,
        ClientManifest overlayManifest,
        string install,
        LauncherProfile profile,
        ProfileState state,
        HashService hashService,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken,
        bool forceRecompute = false)
    {
        var stash = StashDir(install, profile);
        var addonCache = AddonCacheDir(install, profile);
        Directory.CreateDirectory(stash);
        Directory.CreateDirectory(addonCache);

        var mpqs = new List<string>();
        var addons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var total = overlayManifest.Files.Count;
        var index = 0;
        long bytesDone = 0;
        var toDownload = new List<(ManifestFile File, string Destination, string? LivePath, string CacheKey)>();

        foreach (var file in overlayManifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = file.RelativePath.Replace('\\', '/');

            if (rel.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            {
                var underData = rel.Substring("Data/".Length);
                if (!SharedClientDataFiles.IsProfileOverlayMpq(underData))
                {
                    continue;
                }

                // Overlay manifests only list per-stack patch MPQs (managed content). Never skip here:
                // server patches are commonly named patch-1.MPQ, patch-2.MPQ, etc. and must be stashed.
                var dest = SafeCombine(stash, underData);
                var livePath = Path.Combine(DataDir(install), underData.Replace('/', Path.DirectorySeparatorChar));
                mpqs.Add(underData);
                toDownload.Add((file, dest, livePath, $"overlay/{profile.FolderName}/{underData}"));
            }
            else if (rel.StartsWith("Interface/AddOns/", StringComparison.OrdinalIgnoreCase))
            {
                var underAddons = rel.Substring("Interface/AddOns/".Length);
                var addonName = underAddons.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(addonName))
                {
                    addons.Add(addonName);
                }

                var dest = SafeCombine(addonCache, underAddons);
                toDownload.Add((file, dest, null, $"overlay-addon/{profile.FolderName}/{underAddons}"));
            }
        }

        if (toDownload.Count > 0)
        {
            progress?.Report(new SyncProgress
            {
                Status = $"Downloading server files (0/{toDownload.Count})",
                FilesCompleted = 0,
                FilesTotal = toDownload.Count,
                Fraction = 0
            });
        }

        foreach (var (file, dest, livePath, cacheKey) in toDownload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            progress?.Report(new SyncProgress
            {
                Status = $"Downloading server files ({index}/{toDownload.Count})",
                FilesCompleted = index - 1,
                FilesTotal = toDownload.Count,
                Fraction = toDownload.Count == 0 ? 1 : (double)(index - 1) / toDownload.Count
            });

            var needs = await NeedsDownloadAsync(file, dest, cacheKey, hashService, forceRecompute, cancellationToken);
            if (needs && livePath is not null && File.Exists(livePath))
            {
                var liveHash = await hashService.GetHashAsync(
                    $"{cacheKey}:live",
                    livePath,
                    forceRecompute,
                    cancellationToken);
                if (string.Equals(liveHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(livePath, dest, overwrite: true);
                    hashService.UpdateCache(cacheKey, dest, file.Sha256);
                    needs = false;
                }
            }

            if (needs)
            {
                long last = 0;
                var fileProgress = new Progress<long>(_ => { });
                await overlayClient.DownloadFileAsync(file.RelativePath, dest, file.Size, fileProgress, cancellationToken);

                var actual = await hashService.GetHashAsync(cacheKey, dest, forceRecompute: true, cancellationToken);
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Overlay file failed verification: {file.RelativePath}");
                }

                hashService.UpdateCache(cacheKey, dest, file.Sha256);
                _ = last;
            }

            bytesDone += file.Size;
            progress?.Report(new SyncProgress
            {
                Status = $"Downloading server files ({index}/{toDownload.Count})",
                FilesCompleted = index,
                FilesTotal = toDownload.Count,
                Fraction = toDownload.Count == 0 ? 1 : (double)index / toDownload.Count
            });
        }

        PruneStash(stash, mpqs);
        PruneAddonCache(addonCache, addons);
        PruneRetiredLiveOverlayMpqs(install, overlayManifest);

        state.OverlayMpqs = mpqs;
        state.DownloadedAddons = addons.ToList();
        // Drop enabled addons that no longer exist server-side.
        state.EnabledAddons = state.EnabledAddons.Where(addons.Contains).ToList();
        state.LastOverlayVersion = overlayManifest.Version;
    }

    /// <summary>
    /// Deactivates the currently-active profile: moves its overlay MPQs from the live Data/ root back
    /// into its stash and its enabled addons back into the addon cache.
    /// </summary>
    public void Deactivate(string install, LauncherState state, IReadOnlyList<LauncherProfile> profiles)
    {
        var activeId = state.ActiveProfileId;
        if (string.IsNullOrEmpty(activeId) || !state.Profiles.TryGetValue(activeId, out var profileState))
        {
            state.ActiveProfileId = null;
            return;
        }

        var profile = profiles.FirstOrDefault(p => p.StackId == activeId) ?? new LauncherProfile { StackId = activeId };
        var stash = StashDir(install, profile);
        Directory.CreateDirectory(stash);

        foreach (var mpq in profileState.OverlayMpqs)
        {
            if (!SharedClientDataFiles.IsProfileOverlayMpq(mpq))
            {
                continue;
            }

            var live = Path.Combine(DataDir(install), mpq.Replace('/', Path.DirectorySeparatorChar));
            var stashed = Path.Combine(stash, mpq.Replace('/', Path.DirectorySeparatorChar));
            MoveFile(live, stashed);
        }

        var cache = AddonCacheDir(install, profile);
        foreach (var addon in profileState.EnabledAddons)
        {
            var live = Path.Combine(LiveAddonsDir(install), addon);
            var cached = Path.Combine(cache, "Interface", "AddOns", addon);
            MoveDirectory(live, cached);
        }

        state.ActiveProfileId = null;
    }

    /// <summary>
    /// Activates a profile: deletes the Cache/ folder, moves the profile's stashed MPQs into Data/ and
    /// its enabled addons into Interface/AddOns.
    /// </summary>
    public void Activate(string install, LauncherProfile profile, LauncherState state)
    {
        var profileState = state.GetProfile(profile.StackId);

        RestoreMisplacedSharedBaseMpqs(install, profile);

        ReconcileOverlayDuplicates(install, profile, profileState.OverlayMpqs);

        // Fresh cache avoids stale MPQ indexing across profile switches.
        ClearClientCache(install);
        PruneLiveOverlayMpqs(install, profileState.OverlayMpqs);

        var stash = StashDir(install, profile);
        foreach (var mpq in profileState.OverlayMpqs)
        {
            if (!SharedClientDataFiles.IsProfileOverlayMpq(mpq))
            {
                continue;
            }

            var stashed = Path.Combine(stash, mpq.Replace('/', Path.DirectorySeparatorChar));
            var live = Path.Combine(DataDir(install), mpq.Replace('/', Path.DirectorySeparatorChar));
            MoveFile(stashed, live);
        }

        var cache = AddonCacheDir(install, profile);
        foreach (var addon in profileState.EnabledAddons)
        {
            var cached = Path.Combine(cache, "Interface", "AddOns", addon);
            var live = Path.Combine(LiveAddonsDir(install), addon);
            MoveDirectory(cached, live);
        }

        state.ActiveProfileId = profile.StackId;
    }

    /// <summary>Enables/disables a single addon for a profile, moving it between cache and live (no download).</summary>
    public void SetAddonEnabled(string install, LauncherProfile profile, LauncherState state, string addon, bool enabled)
    {
        var profileState = state.GetProfile(profile.StackId);
        var isActive = state.ActiveProfileId == profile.StackId;
        var cache = AddonCacheDir(install, profile);
        var cached = Path.Combine(cache, "Interface", "AddOns", addon);
        var live = Path.Combine(LiveAddonsDir(install), addon);

        if (enabled)
        {
            if (!profileState.EnabledAddons.Contains(addon, StringComparer.OrdinalIgnoreCase))
            {
                profileState.EnabledAddons.Add(addon);
            }

            if (isActive)
            {
                MoveDirectory(cached, live);
            }
        }
        else
        {
            profileState.EnabledAddons.RemoveAll(a => string.Equals(a, addon, StringComparison.OrdinalIgnoreCase));
            if (isActive)
            {
                MoveDirectory(live, cached);
            }
        }
    }

    /// <summary>
    /// Combines a server-provided relative path onto a stash/cache root and verifies the result stays
    /// under that root. Rejects absolute paths and <c>..</c> traversal so a malicious overlay manifest
    /// cannot write outside the profile's stash (defense in depth on top of manifest signature checks).
    /// </summary>
    private static string SafeCombine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            throw new InvalidOperationException("Overlay manifest contained an empty file path.");
        }

        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || (normalized.Length >= 2 && normalized[1] == ':')
            || normalized.Split('/').Any(s => s == ".."))
        {
            throw new InvalidOperationException($"Overlay manifest contained an unsafe file path: {relative}");
        }

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Overlay manifest file path escapes the stash: {relative}");
        }

        return candidate;
    }

    private static async Task<bool> NeedsDownloadAsync(
        ManifestFile file, string localPath, string cacheKey, HashService hashService,
        bool forceRecompute, CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
        {
            return true;
        }

        if (new FileInfo(localPath).Length != file.Size)
        {
            return true;
        }

        // On a full verify, re-hash from disk (ignoring the cache) so corrupt overlay files with an
        // unchanged size/mtime are still detected and re-downloaded.
        var hash = await hashService.GetHashAsync(cacheKey, localPath, forceRecompute, cancellationToken);
        return !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void PruneStash(string stash, IReadOnlyCollection<string> keep)
    {
        if (!Directory.Exists(stash))
        {
            return;
        }

        var keepSet = new HashSet<string>(
            keep.Select(k => Path.Combine(stash, k.Replace('/', Path.DirectorySeparatorChar))),
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(stash, "*", SearchOption.AllDirectories))
        {
            if (!keepSet.Contains(file))
            {
                TryDeleteFile(file);
            }
        }
    }

    private static void PruneAddonCache(string cache, ICollection<string> keepAddons)
    {
        var addonsRoot = Path.Combine(cache, "Interface", "AddOns");
        if (!Directory.Exists(addonsRoot))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(addonsRoot))
        {
            if (!keepAddons.Contains(Path.GetFileName(dir)))
            {
                TryDeleteDirectory(dir);
            }
        }
    }

    private static void MoveFile(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            TryDeleteFile(destination);
        }

        File.Move(source, destination);
    }

    private static void MoveDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        if (Directory.Exists(destination))
        {
            TryDeleteDirectory(destination);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            // Cross-volume or partial: fall back to recursive copy + delete.
            CopyDirectory(source, destination);
            TryDeleteDirectory(source);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } } catch { /* best effort */ }
    }
}
