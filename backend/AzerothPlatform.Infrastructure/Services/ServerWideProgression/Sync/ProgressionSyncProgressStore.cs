using System.Text.Json;

namespace AzerothPlatform.Infrastructure.Services.IndividualProgression;

/// <summary>Persists live progression sync progress on the stack so status can be polled during long runs.</summary>
internal sealed class ProgressionSyncProgressStore
{
    private const string ProgressFileName = "progression_sync_progress.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _stackRoot;
    private readonly List<string> _log = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ProgressionSyncProgressStore(string stackRoot) => _stackRoot = stackRoot;

    public static string ProgressPath(string stackRoot) => Path.Combine(stackRoot, ProgressFileName);

    public static async Task<ProgressionSyncProgressState?> TryLoadAsync(
        string stackRoot,
        CancellationToken cancellationToken = default)
    {
        var path = ProgressPath(stackRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProgressionSyncProgressState>(stream, JsonOptions, cancellationToken);
    }

    public static bool IsActivelyRunning(ProgressionSyncProgressState? progress, string stackRoot) =>
        progress is { IsRunning: true } && !IsStale(progress, stackRoot);

    public static bool IsStale(ProgressionSyncProgressState progress, string stackRoot)
    {
        if (!progress.IsRunning)
        {
            return false;
        }

        var lastActivity = progress.StartedAt ?? DateTimeOffset.UtcNow;
        var path = ProgressPath(stackRoot);
        if (File.Exists(path))
        {
            var fileWrite = File.GetLastWriteTimeUtc(path);
            if (fileWrite > lastActivity)
            {
                lastActivity = fileWrite;
            }
        }

        if (DateTimeOffset.UtcNow - lastActivity > InactivityStaleAfter)
        {
            return true;
        }

        return progress.StartedAt.HasValue
               && DateTimeOffset.UtcNow - progress.StartedAt.Value > AbsoluteStaleAfter;
    }

    public static TimeSpan InactivityStaleAfter { get; } = TimeSpan.FromMinutes(5);

    public static TimeSpan AbsoluteStaleAfter { get; } = TimeSpan.FromMinutes(30);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _log.Clear();
        await PersistAsync(new ProgressionSyncProgressState
        {
            IsRunning = true,
            Phase = "Starting",
            ProgressPercent = 0,
            Message = "Starting progression sync…",
            StartedAt = DateTimeOffset.UtcNow,
            Log = [],
        }, cancellationToken);
    }

    public async Task ReportAsync(
        string phase,
        int progressPercent,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _log.Add(message);
        }

        var existing = await TryLoadAsync(_stackRoot, cancellationToken);
        await PersistAsync(new ProgressionSyncProgressState
        {
            IsRunning = true,
            Phase = phase,
            ProgressPercent = Math.Clamp(progressPercent, 0, 100),
            Message = message,
            StartedAt = existing?.StartedAt ?? DateTimeOffset.UtcNow,
            Log = _log.ToList(),
        }, cancellationToken);
    }

    public async Task CompleteAsync(
        bool success,
        string message,
        string? error,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _log.Add(message);
        }

        await PersistAsync(new ProgressionSyncProgressState
        {
            IsRunning = false,
            Phase = success ? "Completed" : "Failed",
            ProgressPercent = success ? 100 : 0,
            Message = message,
            Error = error,
            CompletedAt = DateTimeOffset.UtcNow,
            Log = _log.ToList(),
        }, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var path = ProgressPath(_stackRoot);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PersistAsync(ProgressionSyncProgressState state, CancellationToken cancellationToken)
    {
        var path = ProgressPath(_stackRoot);
        Directory.CreateDirectory(_stackRoot);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

internal sealed class ProgressionSyncProgressState
{
    public bool IsRunning { get; set; }
    public string Phase { get; set; } = string.Empty;
    public int ProgressPercent { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public List<string> Log { get; set; } = [];
}
