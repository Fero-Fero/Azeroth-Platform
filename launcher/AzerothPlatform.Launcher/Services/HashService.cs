using System.Security.Cryptography;
using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Computes SHA-256 hashes of local files, backed by a cache (keyed by path + size + mtime)
/// so multi-GB files are not rehashed on every launch.
/// </summary>
public sealed class HashService
{
    private readonly Dictionary<string, HashCacheEntry> _cache;

    public HashService(Dictionary<string, HashCacheEntry> cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Returns the SHA-256 of the file at <paramref name="absolutePath"/>, using and updating the
    /// cache under <paramref name="relativeKey"/>. Returns null when the file does not exist. When
    /// <paramref name="forceRecompute"/> is true the cached hash is ignored and the file is re-hashed
    /// from disk: this catches on-disk corruption where the size and mtime are unchanged (so the cache
    /// would otherwise return a stale "good" hash) - used by the full "Verify files" pass.
    /// </summary>
    public async Task<string?> GetHashAsync(
        string relativeKey, string absolutePath, bool forceRecompute, CancellationToken cancellationToken)
    {
        if (!File.Exists(absolutePath))
        {
            _cache.Remove(relativeKey);
            return null;
        }

        var info = new FileInfo(absolutePath);
        var mtimeTicks = info.LastWriteTimeUtc.Ticks;

        if (!forceRecompute)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(relativeKey, out var cached)
                    && cached.Size == info.Length
                    && cached.MTimeTicks == mtimeTicks)
                {
                    return cached.Sha256;
                }
            }
        }

        var hash = await ComputeAsync(absolutePath, cancellationToken);

        lock (_cache)
        {
            _cache[relativeKey] = new HashCacheEntry
            {
                Size = info.Length,
                MTimeTicks = mtimeTicks,
                Sha256 = hash
            };
        }

        return hash;
    }

    /// <summary>Records the expected hash of a freshly downloaded file into the cache.</summary>
    public void UpdateCache(string relativeKey, string absolutePath, string sha256)
    {
        if (!File.Exists(absolutePath))
        {
            return;
        }

        var info = new FileInfo(absolutePath);
        lock (_cache)
        {
            _cache[relativeKey] = new HashCacheEntry
            {
                Size = info.Length,
                MTimeTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = sha256
            };
        }
    }

    private static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
