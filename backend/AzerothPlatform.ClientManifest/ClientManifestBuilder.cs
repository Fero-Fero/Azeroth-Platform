using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.ClientContent;

/// <summary>
/// The result of a manifest scan: the manifest itself plus a resolver mapping each manifest path to
/// the absolute file that backs it (overlay wins over base), so a server can stream the right file.
/// </summary>
public sealed class ClientManifestResult
{
    public required Core.Contracts.ClientManifest Manifest { get; init; }

    /// <summary>Map of forward-slash relative manifest path -> absolute source path (overlay overrides base).</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }
}

/// <summary>
/// Shared, dependency-free logic that turns one or more client "game" roots into a
/// <see cref="ClientManifest"/>. This is the single source of truth for the manifest algorithm so the
/// manager (<c>ClientDistributionService</c>) and the standalone client-server container produce
/// byte-identical manifests and launchers stay compatible regardless of who serves the files.
///
/// SHA-256 hashes are cached by path + size + mtime in a writable cache directory so multi-GB clients
/// are not rehashed on every scan. When multiple roots are provided, later roots override earlier ones
/// by relative path (i.e. a per-stack overlay overrides the shared base).
/// </summary>
public static class ClientManifestBuilder
{
    public const string HashCacheFileName = ".hashcache.json";
    public const string ManifestFileName = ".manifest.json";

    private const string AddonsPrefix = "Interface/AddOns/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>The default managed-file prefixes: patch MPQs and installed addons.</summary>
    public static readonly IReadOnlyList<string> DefaultManagedPrefixes = new[] { "Data/patch-", "Interface/AddOns/" };

    /// <summary>
    /// Scans the given game roots (later roots override earlier by relative path), builds a manifest,
    /// and returns a resolver for serving files. Bookkeeping files and the per-player <c>WTF/</c> tree
    /// are excluded exactly as the launcher expects.
    /// </summary>
    public static async Task<ClientManifestResult> BuildAsync(
        IReadOnlyList<string> gameRoots,
        string cacheDirectory,
        IReadOnlyList<string> managedPrefixes,
        string verifyToken,
        bool persistManifest = true,
        CancellationToken cancellationToken = default,
        string? signingPrivateKey = null)
    {
        Directory.CreateDirectory(cacheDirectory);

        var hashCache = LoadHashCache(cacheDirectory);
        var updatedCache = new Dictionary<string, HashCacheEntry>(StringComparer.Ordinal);

        // Resolve files across roots; later roots (overlay) override earlier roots (base) by exact path.
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in gameRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var absolutePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relativePath = ToManifestPath(Path.GetRelativePath(root, absolutePath));

                // Skip our own bookkeeping files.
                if (relativePath is HashCacheFileName or ManifestFileName)
                {
                    continue;
                }

                // Never distribute the client's WTF folder: it holds per-player runtime state
                // (Config.wtf, per-account SavedVariables). Server-owned values reach the client via the
                // rendered Config.wtf settings template the launcher merges in.
                if (IsPlayerRuntimeState(relativePath))
                {
                    continue;
                }

                resolved[relativePath] = absolutePath;
            }
        }

        var files = new List<ManifestFile>(resolved.Count);
        foreach (var (relativePath, absolutePath) in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(absolutePath);
            var mtimeTicks = info.LastWriteTimeUtc.Ticks;

            string sha256;
            if (hashCache.TryGetValue(relativePath, out var cachedEntry)
                && cachedEntry.Size == info.Length
                && cachedEntry.MTimeTicks == mtimeTicks)
            {
                sha256 = cachedEntry.Sha256;
            }
            else
            {
                sha256 = await ComputeSha256Async(absolutePath, cancellationToken);
            }

            updatedCache[relativePath] = new HashCacheEntry
            {
                Size = info.Length,
                MTimeTicks = mtimeTicks,
                Sha256 = sha256
            };

            files.Add(new ManifestFile
            {
                RelativePath = relativePath,
                Size = info.Length,
                Sha256 = sha256,
                Group = ResolveGroup(managedPrefixes, relativePath)
            });
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        var manifest = new Core.Contracts.ClientManifest
        {
            Version = ComputeManifestVersion(files),
            VerifyToken = verifyToken,
            GeneratedAt = DateTime.UtcNow,
            TotalSize = files.Sum(f => f.Size),
            Files = files
        };

        // Sign the completed manifest so launchers can detect any tampering of files/hashes even when
        // the file content is served over plain HTTP by a separate client-server container.
        ManifestSigner.Sign(manifest, signingPrivateKey);

        PersistHashCache(cacheDirectory, updatedCache);
        if (persistManifest)
        {
            PersistManifest(cacheDirectory, manifest);
        }

        return new ClientManifestResult { Manifest = manifest, Files = resolved };
    }

    /// <summary>
    /// Classifies a manifest path into <see cref="ManifestFileGroup.Base"/> or
    /// <see cref="ManifestFileGroup.Managed"/> using the same rules everywhere: default client addons
    /// are base, files under a managed prefix are managed, everything else is base.
    /// </summary>
    public static ManifestFileGroup ResolveGroup(IReadOnlyList<string> managedPrefixes, string relativePath)
    {
        // Standard Blizzard MPQs match the managed "Data/patch-" prefix but are shared base client
        // content — never per-profile overlay (see launcher README).
        if (SharedClientDataFiles.IsSharedBaseDataFile(relativePath))
        {
            return ManifestFileGroup.Base;
        }

        // Default client addons (Blizzard_* UI modules and AIO) ship as part of the base client: they
        // are delivered by default but must not be surfaced in the addon manager or pruned.
        if (IsDefaultClientAddon(relativePath))
        {
            return ManifestFileGroup.Base;
        }

        foreach (var prefix in managedPrefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix)
                && relativePath.StartsWith(prefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return ManifestFileGroup.Managed;
            }
        }

        return ManifestFileGroup.Base;
    }

    /// <summary>
    /// True when a manifest path belongs to a default client addon (a <c>Blizzard_*</c> module or the
    /// <c>AIO</c> framework) under <c>Interface/AddOns/</c>. These are treated as base client content.
    /// </summary>
    private static bool IsDefaultClientAddon(string relativePath)
    {
        if (!relativePath.StartsWith(AddonsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = relativePath[AddonsPrefix.Length..];
        var addonName = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(addonName))
        {
            return false;
        }

        return addonName.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)
            || addonName.Equals("Blizzard", StringComparison.OrdinalIgnoreCase)
            || addonName.Equals("AIO", StringComparison.OrdinalIgnoreCase)
            || addonName.StartsWith("AIO_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for client files that are per-player runtime state and must never be distributed: the
    /// entire <c>WTF/</c> tree (Config.wtf, per-account SavedVariables, ...).
    /// </summary>
    public static bool IsPlayerRuntimeState(string relativePath) =>
        relativePath.StartsWith("WTF/", StringComparison.OrdinalIgnoreCase);

    public static string ToManifestPath(string relativePath) => relativePath.Replace('\\', '/');

    public static string ComputeManifestVersion(IEnumerable<ManifestFile> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files)
        {
            builder.Append(file.RelativePath).Append(':').Append(file.Sha256).Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    public static Dictionary<string, HashCacheEntry> LoadHashCache(string cacheDirectory)
    {
        var path = Path.Combine(cacheDirectory, HashCacheFileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, HashCacheEntry>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<Dictionary<string, HashCacheEntry>>(json, JsonOptions);
            return cache is null
                ? new Dictionary<string, HashCacheEntry>(StringComparer.Ordinal)
                : new Dictionary<string, HashCacheEntry>(cache, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, HashCacheEntry>(StringComparer.Ordinal);
        }
    }

    public static void PersistHashCache(string cacheDirectory, Dictionary<string, HashCacheEntry> cache)
    {
        try
        {
            var path = Path.Combine(cacheDirectory, HashCacheFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch
        {
            // Non-fatal: hashes are recomputed on the next scan.
        }
    }

    public static void PersistManifest(string cacheDirectory, Core.Contracts.ClientManifest manifest)
    {
        try
        {
            var path = Path.Combine(cacheDirectory, ManifestFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        }
        catch
        {
            // Non-fatal snapshot.
        }
    }

    public sealed class HashCacheEntry
    {
        public long Size { get; set; }
        public long MTimeTicks { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
