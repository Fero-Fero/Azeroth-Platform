using System.Collections.Concurrent;
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
    private const string OnDemandTag = "on-demand";

    private readonly string _storeDir;
    private readonly IWdbxCli _wdbx;
    private readonly ILogger<DbcBaselineStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _logs = new();
    private volatile bool _inProgress;
    private string? _error;
    private string? _message = "DBC baselines convert on demand from the stack data directory.";

    public DbcBaselineStore(
        IOptions<DockerOptions> dockerOptions,
        IWdbxCli wdbx,
        ILogger<DbcBaselineStore> logger)
    {
        var buildsPath = dockerOptions.Value.BuildsPath;
        var buildsFull = Path.IsPathRooted(buildsPath) ? buildsPath : Path.GetFullPath(buildsPath);
        var dataDir = Path.GetDirectoryName(buildsFull.TrimEnd(Path.DirectorySeparatorChar)) ?? buildsFull;
        _storeDir = Path.Combine(dataDir, "dbc-store");
        _wdbx = wdbx;
        _logger = logger;
        Directory.CreateDirectory(_storeDir);
    }

    public string? StoreDirectory => _storeDir;

    public bool IsReady() => true;

    public DbcBaselineStoreDto GetStatus() => new()
    {
        Ready = true,
        InProgress = _inProgress,
        Tag = OnDemandTag,
        SyncedAt = NewestCsvWriteTime(),
        TableCount = CachedTableCount(),
        Error = _error,
        Message = _message,
        RecentLogs = _logs.ToArray()
    };

    public string? FindTableCsv(string tableName)
    {
        var expected = CsvNormalizer.TableFileName(tableName);
        var path = Path.Combine(_storeDir, expected);
        return File.Exists(path) ? path : null;
    }

    public async Task<string?> EnsureTableCsvAsync(
        string tableName, string dbcPath, CancellationToken cancellationToken = default)
    {
        var table = CsvNormalizer.NormalizeTableName(tableName);
        var csvPath = Path.Combine(_storeDir, CsvNormalizer.TableFileName(table));
        if (File.Exists(csvPath)
            && File.Exists(dbcPath)
            && File.GetLastWriteTimeUtc(csvPath) >= File.GetLastWriteTimeUtc(dbcPath))
        {
            return csvPath;
        }

        if (!File.Exists(dbcPath))
        {
            return FindTableCsv(table);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(csvPath)
                && File.GetLastWriteTimeUtc(csvPath) >= File.GetLastWriteTimeUtc(dbcPath))
            {
                return csvPath;
            }

            Directory.CreateDirectory(_storeDir);
            try
            {
                await _wdbx.ExportDbcToCsvAsync(dbcPath, csvPath, cancellationToken);
            }
            catch (WdbxDefinitionMissingException ex)
            {
                _logger.LogWarning(ex, "No WDBX definition for {Table}; skipping baseline CSV.", table);
                Log($"Skipping {table}: no WDBX definition for this client build.", onProgress: null);
                return null;
            }

            Log($"Cached baseline CSV for {table}.", onProgress: null);
            _message = $"Cached {CachedTableCount()} table(s) on demand.";
            return csvPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public DbcBaselineStoreDto EnqueueSync(bool force = false)
    {
        if (force)
        {
            _ = Task.Run(() => SyncAsync(force: true, onProgress: null, CancellationToken.None));
        }

        return GetStatus();
    }

    public async Task SyncAsync(bool force, Action<string>? onProgress, CancellationToken cancellationToken = default)
    {
        if (!force)
        {
            _message = "DBC baselines convert on demand from the stack data directory.";
            Log(_message, onProgress);
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _inProgress = true;
            _error = null;
            ClearCachedCsvs();
            _message = "Cleared on-demand DBC CSV cache.";
            Log(_message, onProgress);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _logger.LogError(ex, "Failed to clear DBC CSV cache.");
            throw;
        }
        finally
        {
            _inProgress = false;
            _gate.Release();
        }
    }

    private int CachedTableCount() =>
        Directory.Exists(_storeDir) ? Directory.EnumerateFiles(_storeDir, "*.txt").Count() : 0;

    private DateTime? NewestCsvWriteTime()
    {
        if (!Directory.Exists(_storeDir))
        {
            return null;
        }

        DateTime? newest = null;
        foreach (var file in Directory.EnumerateFiles(_storeDir, "*.txt"))
        {
            var time = File.GetLastWriteTimeUtc(file);
            if (newest is null || time > newest)
            {
                newest = time;
            }
        }

        return newest;
    }

    private void ClearCachedCsvs()
    {
        if (!Directory.Exists(_storeDir))
        {
            return;
        }

        foreach (var csv in Directory.EnumerateFiles(_storeDir, "*.txt"))
        {
            File.Delete(csv);
        }

        var manifest = Path.Combine(_storeDir, "manifest.json");
        if (File.Exists(manifest))
        {
            File.Delete(manifest);
        }
    }

    private void Log(string message, Action<string>? onProgress)
    {
        var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        _logs.Enqueue(stamped);
        while (_logs.Count > MaxLogLines && _logs.TryDequeue(out _))
        {
        }

        onProgress?.Invoke(message);
        _logger.LogInformation("{Message}", message);
    }
}
