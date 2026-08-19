using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Services.Modules.Install;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.DbcStore;

public sealed class DbcBaselineStore : IDbcBaselineStore
{
    private const int MaxLogLines = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _storeDir;
    private readonly string _manifestPath;
    private readonly WowgamingClientDataClient _client;
    private readonly IWdbxCli _wdbx;
    private readonly ILogger<DbcBaselineStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _logs = new();

    private volatile bool _inProgress;
    private string? _error;
    private string? _message;

    public DbcBaselineStore(
        IOptions<DockerOptions> dockerOptions,
        WowgamingClientDataClient client,
        IWdbxCli wdbx,
        ILogger<DbcBaselineStore> logger)
    {
        var buildsPath = dockerOptions.Value.BuildsPath;
        var buildsFull = Path.IsPathRooted(buildsPath) ? buildsPath : Path.GetFullPath(buildsPath);
        var dataDir = Path.GetDirectoryName(buildsFull.TrimEnd(Path.DirectorySeparatorChar)) ?? buildsFull;
        _storeDir = Path.Combine(dataDir, "dbc-store");
        _manifestPath = Path.Combine(_storeDir, "manifest.json");
        _client = client;
        _wdbx = wdbx;
        _logger = logger;
        Directory.CreateDirectory(_storeDir);
    }

    public string? StoreDirectory => IsReady() ? _storeDir : null;

    public DbcBaselineStoreDto GetStatus()
    {
        var manifest = ReadManifest();
        var tableCount = manifest?.TableCount
            ?? Directory.EnumerateFiles(_storeDir, "*.txt").Count();
        return new DbcBaselineStoreDto
        {
            Ready = IsReady(),
            InProgress = _inProgress,
            Tag = manifest?.Tag,
            PublishedAt = manifest?.PublishedAt,
            SyncedAt = manifest?.SyncedAt,
            TableCount = tableCount,
            Error = _error,
            Message = _message,
            RecentLogs = _logs.ToArray()
        };
    }

    public bool IsReady() =>
        File.Exists(_manifestPath) && Directory.EnumerateFiles(_storeDir, "*.txt").Any();

    public string? FindTableCsv(string tableName)
    {
        if (!IsReady())
        {
            return null;
        }

        var expected = CsvNormalizer.TableFileName(tableName);
        var match = Directory.EnumerateFiles(_storeDir, "*.txt")
            .FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase));
        return match;
    }

    public DbcBaselineStoreDto EnqueueSync(bool force = false)
    {
        if (_inProgress)
        {
            return GetStatus();
        }

        if (!force && IsReady())
        {
            _ = Task.Run(() => SyncIfNewerAsync(CancellationToken.None));
            return GetStatus();
        }

        _ = Task.Run(() => SyncAsync(force, onProgress: null, CancellationToken.None));
        return GetStatus();
    }

    public async Task SyncAsync(bool force, Action<string>? onProgress, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _inProgress = true;
            _error = null;
            Log("Starting DBC baseline sync…", onProgress);

            if (!force && IsReady())
            {
                try
                {
                    var latest = await _client.GetLatestReleaseAsync(cancellationToken);
                    var current = ReadManifest();
                    if (current is not null
                        && string.Equals(current.Tag, latest.Tag, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"DBC store already on {latest.Tag}; skipping.", onProgress);
                        _message = $"Already on {latest.Tag}";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not check latest wowgaming/client-data tag; continuing sync.");
                }
            }

            await SyncCoreAsync(onProgress, cancellationToken);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _message = "DBC baseline sync failed.";
            Log($"DBC baseline sync failed: {ex.Message}", onProgress);
            _logger.LogError(ex, "DBC baseline store sync failed");
            throw;
        }
        finally
        {
            _inProgress = false;
            _gate.Release();
        }
    }

    private async Task SyncIfNewerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SyncAsync(force: false, onProgress: null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background DBC store refresh failed");
        }
    }

    private async Task SyncCoreAsync(Action<string>? onProgress, CancellationToken cancellationToken)
    {
        var latest = await _client.GetLatestReleaseAsync(cancellationToken);
        Log($"Latest wowgaming/client-data release is {latest.Tag}.", onProgress);

        EnsureDiskSpace(latest.SizeBytes);

        var work = Path.Combine(_storeDir, ".sync-tmp");
        TryDelete(work);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, WowgamingClientDataClient.AssetName);
        var extractDir = Path.Combine(work, "dbc");
        Directory.CreateDirectory(extractDir);

        try
        {
            await _client.DownloadAsync(latest.DownloadUrl, zipPath, onProgress, cancellationToken);
            Log("Extracting DBC files from Data.zip (maps/vmaps discarded)…", onProgress);
            var extracted = ExtractDbcEntries(zipPath, extractDir, cancellationToken);
            if (extracted == 0)
            {
                throw new InvalidOperationException(
                    "Data.zip did not contain any dbc/*.dbc files. The wowgaming/client-data layout may have changed.");
            }

            Log($"Exporting {extracted} DBC table(s) to CSV…", onProgress);
            ClearStoreFiles();

            var exported = 0;
            foreach (var dbc in Directory.EnumerateFiles(extractDir, "*.dbc"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = CsvNormalizer.NormalizeTableName(Path.GetFileName(dbc));
                var csv = Path.Combine(_storeDir, CsvNormalizer.TableFileName(table));
                await _wdbx.ExportDbcToCsvAsync(dbc, csv, cancellationToken);
                exported++;
                if (exported % 25 == 0)
                {
                    Log($"Exported {exported} / {extracted} tables…", onProgress);
                }
            }

            var manifest = new DbcBaselineManifest
            {
                Tag = latest.Tag,
                PublishedAt = latest.PublishedAt,
                SyncedAt = DateTime.UtcNow,
                TableCount = exported
            };
            await File.WriteAllTextAsync(_manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
            _message = $"Synced {exported} tables from {latest.Tag}.";
            Log(_message, onProgress);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static int ExtractDbcEntries(string zipPath, string extractDir, CancellationToken cancellationToken)
    {
        var destFull = Path.GetFullPath(extractDir);
        var destPrefix = destFull.EndsWith(Path.DirectorySeparatorChar)
            ? destFull
            : destFull + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        var count = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)
                || !entry.Name.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = entry.FullName.Replace('\\', '/');
            if (!IsDbcPath(normalized))
            {
                continue;
            }

            var dest = Path.GetFullPath(Path.Combine(extractDir, Path.GetFileName(entry.Name)));
            if (!dest.StartsWith(destPrefix, StringComparison.Ordinal)
                && !string.Equals(dest, destFull, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Zip entry escapes the extract directory: {entry.FullName}");
            }

            entry.ExtractToFile(dest, overwrite: true);
            count++;
        }

        return count;
    }

    private static bool IsDbcPath(string normalized)
    {
        var lower = normalized.ToLowerInvariant();
        return lower.Contains("/dbc/") || lower.StartsWith("dbc/", StringComparison.Ordinal);
    }

    private void ClearStoreFiles()
    {
        if (File.Exists(_manifestPath))
        {
            File.Delete(_manifestPath);
        }

        foreach (var csv in Directory.EnumerateFiles(_storeDir, "*.txt"))
        {
            File.Delete(csv);
        }
    }

    private DbcBaselineManifest? ReadManifest()
    {
        if (!File.Exists(_manifestPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DbcBaselineManifest>(File.ReadAllText(_manifestPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void EnsureDiskSpace(long zipBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_storeDir));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            // Zip + extracted DBCs + CSV copies. Require ~3x the zip size as a conservative floor.
            var needed = Math.Max(zipBytes * 3, 4L * 1024 * 1024 * 1024);
            if (drive.AvailableFreeSpace < needed)
            {
                throw new InvalidOperationException(
                    $"Not enough disk space to sync the DBC baseline (need about {needed / (1024 * 1024 * 1024)} GB free).");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // DriveInfo is unavailable on some volume layouts; skip the check.
        }
    }

    private void Log(string message, Action<string>? onProgress)
    {
        var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        _logs.Enqueue(stamped);
        while (_logs.Count > MaxLogLines && _logs.TryDequeue(out _))
        {
        }

        _message = message;
        onProgress?.Invoke(message);
        _logger.LogInformation("{Message}", message);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
