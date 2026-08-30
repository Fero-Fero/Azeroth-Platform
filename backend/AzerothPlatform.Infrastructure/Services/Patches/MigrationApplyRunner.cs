using System.Collections.Concurrent;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Patches;

/// <summary>
/// Runs patch apply / reapply operations on a background task with a DB-backed cross-user lock and a
/// persisted, downloadable trace log. Registered as a singleton (like the launcher build service) so a
/// second operator - even on another machine hitting the same manager - is blocked while a run is in
/// flight and can poll live progress. Modeled on <c>LauncherBuildService</c>.
/// </summary>
public sealed class MigrationApplyRunner : IMigrationApplyRunner
{
    private const int MaxMemoryLogLines = 2000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DockerOptions _dockerOptions;
    private readonly ILogger<MigrationApplyRunner> _logger;

    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    public MigrationApplyRunner(
        IServiceScopeFactory scopeFactory,
        IOptions<DockerOptions> dockerOptions,
        ILogger<MigrationApplyRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _dockerOptions = dockerOptions.Value;
        _logger = logger;
    }

    private string BaseDir => Path.IsPathRooted(_dockerOptions.BuildsPath)
        ? _dockerOptions.BuildsPath
        : Path.GetFullPath(_dockerOptions.BuildsPath);

    private string StackRoot(string stackId) => Path.Combine(BaseDir, stackId);

    public Task<ApplyStatusDto> StartApplyAsync(string stackId, string patchKey, CancellationToken cancellationToken = default) =>
        StartAsync(stackId, patchKey, (svc, ct) => svc.ApplyPatchAsync(stackId, patchKey, ct), cancellationToken);

    public Task<ApplyStatusDto> StartReapplyAllAsync(string stackId, CancellationToken cancellationToken = default) =>
        StartAsync(stackId, "*", (svc, ct) => svc.ReapplyAllAsync(stackId, ct), cancellationToken);

    private async Task<ApplyStatusDto> StartAsync(
        string stackId, string patchKey,
        Func<IMigrationService, CancellationToken, Task<ApplyPatchResultDto>> operation,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");

        // Atomically claim the DB lock: only succeeds if no live lock is held (idle or stale). This is
        // a single UPDATE statement, so two concurrent claimers cannot both win.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            if (!await db.ManagedStacks.AnyAsync(s => s.Id == stackId, cancellationToken))
            {
                throw new KeyNotFoundException($"Stack not found: {stackId}");
            }

            var staleThreshold = DateTime.UtcNow - MigrationService.ApplyLockStaleAfter;
            var claimed = await db.ManagedStacks
                .Where(s => s.Id == stackId
                            && (s.ApplyingPatchKey == null || s.ApplyStartedAt == null || s.ApplyStartedAt < staleThreshold))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.ApplyingPatchKey, patchKey)
                    .SetProperty(s => s.ApplyRunId, runId)
                    .SetProperty(s => s.ApplyStartedAt, DateTime.UtcNow), cancellationToken);

            if (claimed == 0)
            {
                throw new InvalidOperationException(
                    "A patch apply is already in progress for this stack (possibly started by another session).");
            }
        }

        var logPath = BuildLogPath(stackId, patchKey, runId);
        var state = new RunState(runId, patchKey, logPath, MaxMemoryLogLines);
        _runs[stackId] = state;

        _logger.LogInformation(
            "Started background apply for stack {StackId} (patch {PatchKey}, run {RunId})", stackId, patchKey, runId);

        _ = Task.Run(() => ExecuteAsync(stackId, patchKey, runId, state, operation));
        return state.Snapshot();
    }

    private async Task ExecuteAsync(
        string stackId, string patchKey, string runId, RunState state,
        Func<IMigrationService, CancellationToken, Task<ApplyPatchResultDto>> operation)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IMigrationService>();
            svc.SetApplyProgress(state);

            var result = await operation(svc, CancellationToken.None);
            state.Complete(result);
            _logger.LogInformation(
                "Background apply finished for stack {StackId} (run {RunId}): success={Success}",
                stackId, runId, result.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background apply crashed for stack {StackId} (run {RunId})", stackId, runId);
            state.Fail(ex);
        }
        finally
        {
            await ReleaseLockAsync(stackId, runId);
            state.Close();
        }
    }

    /// <summary>Releases the DB lock, but only if it is still held by this run (avoids clobbering a reclaim).</summary>
    private async Task ReleaseLockAsync(string stackId, string runId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            await db.ManagedStacks
                .Where(s => s.Id == stackId && s.ApplyRunId == runId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.ApplyingPatchKey, (string?)null)
                    .SetProperty(s => s.ApplyRunId, (string?)null)
                    .SetProperty(s => s.ApplyStartedAt, (DateTime?)null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release apply lock for stack {StackId} (run {RunId})", stackId, runId);
        }
    }

    public ApplyStatusDto GetStatus(string stackId) =>
        _runs.TryGetValue(stackId, out var state) ? state.Snapshot() : new ApplyStatusDto { IsApplying = false };

    public (string Path, string FileName)? GetLogFile(string stackId, string? runId)
    {
        if (_runs.TryGetValue(stackId, out var state)
            && (runId is null || string.Equals(runId, state.RunId, StringComparison.Ordinal))
            && File.Exists(state.LogFilePath))
        {
            return (state.LogFilePath, Path.GetFileName(state.LogFilePath));
        }

        // Fall back to the on-disk logs (survives a manager restart that dropped the in-memory state).
        var migrationsRoot = MigrationLayout.MigrationsRoot(StackRoot(stackId));
        if (Directory.Exists(migrationsRoot))
        {
            var pattern = runId is null ? "apply-*.log" : $"*{runId}.log";
            var match = Directory.EnumerateFiles(migrationsRoot, pattern, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (match is not null)
            {
                return (match, Path.GetFileName(match));
            }
        }

        return null;
    }

    private string BuildLogPath(string stackId, string patchKey, string runId)
    {
        var folder = patchKey == "*" ? "_reapply-all" : patchKey;
        var logsDir = Path.Combine(MigrationLayout.MigrationsRoot(StackRoot(stackId)), folder, "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, $"apply-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{runId}.log");
    }

    /// <summary>
    /// Per-run state: in-memory log tail + phase for polling, a persisted trace-log file for download,
    /// and the terminal success/error. Implements <see cref="IApplyProgressSink"/> so the apply pipeline
    /// streams lines and stage transitions straight into it.
    /// </summary>
    private sealed class RunState : IApplyProgressSink
    {
        private readonly object _lock = new();
        private readonly List<string> _log = new();
        private readonly int _maxLines;
        private readonly StreamWriter? _writer;

        public string RunId { get; }
        public string PatchKey { get; }
        public string LogFilePath { get; }

        private string? _phase;
        private string? _correlationId;
        private readonly DateTime _startedAt = DateTime.UtcNow;
        private DateTime? _completedAt;
        private bool _running = true;
        private bool? _success;
        private string? _error;

        public RunState(string runId, string patchKey, string logFilePath, int maxLines)
        {
            RunId = runId;
            PatchKey = patchKey;
            LogFilePath = logFilePath;
            _maxLines = maxLines;
            try
            {
                _writer = new StreamWriter(new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
                _writer.WriteLine($"# Apply trace log - patch {patchKey}, run {runId}, started {_startedAt:O}");
            }
            catch
            {
                _writer = null; // logging to file is best-effort; the run still proceeds.
            }
        }

        public void Log(string line) => Append(line);

        public void Stage(string stage)
        {
            lock (_lock) { _phase = stage; }
            Append($"=== stage: {stage} ===");
        }

        private void Append(string line)
        {
            var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {line}";
            lock (_lock)
            {
                _log.Add(stamped);
                if (_log.Count > _maxLines)
                {
                    _log.RemoveRange(0, _log.Count - _maxLines);
                }

                try { _writer?.WriteLine(stamped); } catch { /* best-effort */ }
            }
        }

        public void Complete(ApplyPatchResultDto result)
        {
            lock (_lock)
            {
                _running = false;
                _completedAt = DateTime.UtcNow;
                _success = result.Success;
                _error = result.Error;
                _correlationId = result.CorrelationId;
                _phase = result.Success ? "completed" : "failed";
            }
        }

        public void Fail(Exception ex)
        {
            Append($"ERROR: {ex.Message}");
            lock (_lock)
            {
                _running = false;
                _completedAt = DateTime.UtcNow;
                _success = false;
                _error = ex.Message;
                _phase = "failed";
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                try { _writer?.Flush(); _writer?.Dispose(); } catch { /* best-effort */ }
            }
        }

        public ApplyStatusDto Snapshot()
        {
            lock (_lock)
            {
                return new ApplyStatusDto
                {
                    IsApplying = _running,
                    PatchKey = PatchKey,
                    RunId = RunId,
                    Phase = _phase,
                    CorrelationId = _correlationId,
                    StartedAt = _startedAt,
                    CompletedAt = _completedAt,
                    Success = _success,
                    Error = _error,
                    Log = new List<string>(_log),
                    LogAvailable = File.Exists(LogFilePath)
                };
            }
        }
    }
}
