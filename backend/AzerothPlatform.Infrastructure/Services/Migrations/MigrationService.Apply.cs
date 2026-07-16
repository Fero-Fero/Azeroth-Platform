using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services.Migrations;

/// <summary>
/// Baseline capture and the incremental apply pipeline for <see cref="MigrationService"/>.
/// </summary>
public sealed partial class MigrationService
{
    private string ComposeProject(string stackId) => DockerComposeOverrideGenerator.GetComposeProjectName(stackId);

    // Container names embed the stack name + id (see DockerComposeOverrideGenerator.GetContainerPrefix)
    // so they must be resolved with the stack name to match the generated container_name values.
    private static string ContainerPrefix(string stackId, string? stackName) =>
        DockerComposeOverrideGenerator.GetContainerPrefix(stackId, stackName);
    private static string DbContainer(string stackId, string? stackName) => $"{ContainerPrefix(stackId, stackName)}-database";
    private static string DbImportContainer(string stackId, string? stackName) => $"{ContainerPrefix(stackId, stackName)}-db-import";

    // The named volume is created by compose under the (id-only) project name, not the container name.
    private string DataVolumeName(string stackId) => $"{ComposeProject(stackId)}_ac-client-data";
    private string RepoPath(string stackId) => Path.Combine(GetStackRoot(stackId), "azerothcore-wotlk");

    // ===== Engine targeting (local vs external stacks) =====

    // Docker CLI prefix ("" for local, "--context {name} " for external) targeting the stack's engine.
    // Set once per apply/reapply/baseline via ResolveEngineContextAsync and consumed by the compose /
    // exec / inspect / ping calls (container-lifecycle + SQL). The WDBX / MPQ / volume tool runs stay on
    // the local engine (they operate on manager-local temp dirs and produce artifacts locally).
    private string _engineContextArg = string.Empty;

    // Raw docker context name for the stack's engine (empty for local stacks). Used to build argv-based
    // exec calls where the "--context foo " string prefix can't be embedded.
    private string _engineContext = string.Empty;

    /// <summary>
    /// Resolves the docker context prefix for the stack's engine so container-lifecycle and SQL commands
    /// run against the remote engine for external stacks. No-op (empty prefix) for local stacks.
    /// </summary>
    private async Task ResolveEngineContextAsync(Data.Entities.ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        _engineContext = stack.DeploymentTarget == DeploymentTarget.External
            ? await _remoteEngine.EnsureContextAsync(stack, cancellationToken)
            : string.Empty;
        _engineContextArg = string.IsNullOrEmpty(_engineContext) ? string.Empty : $"--context {_engineContext} ";
    }

    // ===== Live progress (background apply runner) =====

    private IApplyProgressSink? _applyProgress;

    /// <inheritdoc />
    public void SetApplyProgress(IApplyProgressSink? sink) => _applyProgress = sink;

    /// <summary>Appends a log line to the result and mirrors it to the attached progress sink (if any).</summary>
    private void AddLog(ApplyPatchResultDto result, string message)
    {
        result.Log.Add(message);
        _applyProgress?.Log(message);
    }

    // ===== Baseline capture =====

    public async Task InitializeBaselineAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        await ResolveEngineContextAsync(stack, cancellationToken);

        using var activity = MigrationTelemetry.ActivitySource.StartActivity("migration.init-baseline", ActivityKind.Internal);
        activity?.SetTag("stack.id", stackId);
        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["StackId"] = stackId,
            ["CorrelationId"] = activity?.TraceId.ToString(),
            ["Operation"] = "InitBaseline"
        });

        var serverDbcDir = MigrationLayout.ServerDbcDir(stackRoot);
        var volume = DataVolumeName(stackId);

        _logger.LogInformation("Capturing server_dbc baseline for stack {StackId} from volume {Volume}", stackId, volume);

        try
        {
            await ExtractServerDbcFromVolumeAsync(stack, stackRoot, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "baseline capture failed");
            _logger.LogError("Baseline capture failed for stack {StackId}: {Error}", stackId, ex.Message);
            throw;
        }

        var count = Directory.Exists(serverDbcDir) ? Directory.EnumerateFiles(serverDbcDir, "*.dbc").Count() : 0;
        activity?.SetTag("baseline.dbc_count", count);
        _logger.LogInformation("Captured server_dbc baseline for stack {StackId} ({Count} DBC file(s))", stackId, count);
    }

    /// <summary>
    /// Copies every <c>.dbc</c> from the stack's live data volume (<c>/data/dbc</c>, the running
    /// worldserver's DBCs) into <c>server_dbc/</c>. Shared by baseline capture and the pre-compile
    /// extract stage so DBC edits are always layered on the current live DBC set.
    /// </summary>
    private async Task ExtractServerDbcFromVolumeAsync(Data.Entities.ManagedStackEntity stack, string stackRoot, CancellationToken cancellationToken)
    {
        var serverDbcDir = MigrationLayout.ServerDbcDir(stackRoot);
        Directory.CreateDirectory(serverDbcDir);

        // Pull the live DBC set (/data/dbc) from the stack's data volume back to the manager by streaming
        // a tar (works for both local and external engines). The stack must have started at least once so
        // client-data-init populated the volume.
        var volume = DataVolumeName(stack.Id);
        if (!await _remoteEngine.VolumeExistsAsync(stack, volume, cancellationToken))
        {
            throw new InvalidOperationException(
                "No DBC files found in the data volume; start the stack once so client-data-init populates it.");
        }

        try
        {
            await _remoteEngine.FetchVolumeSubdirAsync(stack, volume, "dbc", serverDbcDir, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "DBC extract from data volume failed; start the stack once so client-data-init populates it. " +
                ex.Message, ex);
        }

        if (!Directory.EnumerateFiles(serverDbcDir, "*.dbc").Any())
        {
            throw new InvalidOperationException(
                "No DBC files found in the data volume; start the stack once so client-data-init populates it.");
        }
    }

    // ===== Apply pipeline =====

    public async Task<ApplyPatchResultDto> ApplyPatchAsync(string stackId, string patchKey, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var patch = RequirePatch(stackRoot, patchKey);
        await ResolveEngineContextAsync(stack, cancellationToken);

        var result = new ApplyPatchResultDto { PatchKey = patch.Key, Level = patch.Level };

        using var activity = MigrationTelemetry.ActivitySource.StartActivity("migration.apply", ActivityKind.Internal);
        activity?.SetTag("stack.id", stackId);
        activity?.SetTag("patch.key", patch.Key);
        activity?.SetTag("patch.level", patch.Level);
        result.CorrelationId = activity?.TraceId.ToString();

        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["StackId"] = stackId,
            ["PatchKey"] = patch.Key,
            ["PatchLevel"] = patch.Level,
            ["CorrelationId"] = result.CorrelationId,
            ["Operation"] = "ApplyPatch"
        });

        // Incremental guard: only the next-lowest patch above the current level may be applied.
        var patches = EnumeratePatches(stackRoot);
        var nextLevel = patches
            .Where(p => p.Level > stack.AppliedPatchLevel)
            .Select(p => (int?)p.Level)
            .Min();

        if (nextLevel is null || patch.Level != nextLevel.Value)
        {
            result.Success = false;
            var currentIndex = FormatCurrentIndex(stack.AppliedPatchLevel);
            var nextIndex = nextLevel.HasValue ? FormatCurrentIndex(nextLevel.Value) : "none";
            var patchIndex = patch.Index.ToIndexString();
            result.Error = patch.Level <= stack.AppliedPatchLevel
                ? $"Patch {patch.Key} is already applied (current index {currentIndex})."
                : $"Patches must be applied incrementally. Next applicable index is {nextIndex}, not {patchIndex}.";
            activity?.SetStatus(ActivityStatusCode.Error, result.Error);
            _logger.LogWarning("Rejected apply of patch {PatchKey} on stack {StackId}: {Reason}", patch.Key, stackId, result.Error);
            return result;
        }

        var (applyAllowed, applyBlockedReason) = await _individualProgression.CheckPatchApplyAllowedAsync(stackId, cancellationToken);
        if (!applyAllowed)
        {
            result.Success = false;
            result.Error = applyBlockedReason;
            activity?.SetStatus(ActivityStatusCode.Error, result.Error);
            _logger.LogWarning("Rejected apply of patch {PatchKey} on stack {StackId}: {Reason}", patch.Key, stackId, result.Error);
            return result;
        }

        var dbcFiles = EnumerateDbcSourceFiles(MigrationLayout.DbcDir(stackRoot, patch.Key))
            .OrderBy(p => p).ToList();
        // CSV/.txt sources must be compiled onto the exported server baseline; direct .dbc uploads are
        // published as-is, so they need neither the server export nor the WDBXEditor compile.
        var dbcCsvFiles = dbcFiles.Where(IsDbcCsvSource).ToList();
        var dbcBinaryFiles = dbcFiles.Where(IsDbcBinary).ToList();

        // The baseline is only required to compile CSVs on top of the live DBCs. A patch that only
        // ships pre-built .dbc files doesn't need it.
        if (dbcCsvFiles.Count > 0 && !IsBaselineInitialized(stackRoot))
        {
            result.Success = false;
            result.Error = "This patch contains DBC CSV edits but the server_dbc baseline has not been captured. Start the stack once, then initialize the baseline.";
            activity?.SetStatus(ActivityStatusCode.Error, result.Error);
            _logger.LogWarning("Rejected apply of patch {PatchKey} on stack {StackId}: baseline not captured", patch.Key, stackId);
            return result;
        }

        var repoPath = RepoPath(stackId);
        if (!Directory.Exists(repoPath))
        {
            result.Success = false;
            result.Error = $"Stack build directory not found: {repoPath}";
            activity?.SetStatus(ActivityStatusCode.Error, result.Error);
            _logger.LogWarning("Rejected apply of patch {PatchKey} on stack {StackId}: {Reason}", patch.Key, stackId, result.Error);
            return result;
        }

        // Only run the stages whose inputs actually exist, so an apply never stops/mutates the server
        // (or rebuilds the client MPQ) when nothing of that kind was uploaded.
        var hasDbcCsv = dbcCsvFiles.Count > 0;
        var hasDbcDirect = dbcBinaryFiles.Count > 0;
        var hasDbc = hasDbcCsv || hasDbcDirect;
        var hasSql = PatchHasSql(stackRoot, patch.Key);
        var hasMap = PatchHasMap(stackRoot, patch.Key);
        var hasMpq = PatchHasMpq(stackRoot, patch.Key);
        // Published MPQs this patch removes from the client overlay. Removal runs in the mpq stage,
        // before any new MPQ files are published, and (like an upload) bumps the client manifest.
        var mpqRemovals = ReadMpqRemovals(stackRoot, patch.Key);
        var hasMpqRemovals = mpqRemovals.Count > 0;
        // Anything that touches the server DB / data volume requires the DB up (for SQL) and the
        // world/auth servers stopped (for DBC/map data-volume writes and SQL).
        var needsDatabase = hasSql;
        var needsServerStop = hasSql || hasDbc || hasMap;

        activity?.SetTag("patch.dbc_files", dbcFiles.Count);
        activity?.SetTag("patch.dbc_csv", dbcCsvFiles.Count);
        activity?.SetTag("patch.dbc_direct", dbcBinaryFiles.Count);
        activity?.SetTag("patch.has_sql", hasSql);
        activity?.SetTag("patch.has_map", hasMap);
        activity?.SetTag("patch.has_mpq", hasMpq);

        var overallStopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "Applying patch {PatchKey} (level {Level}) to stack {StackId}: {DbcCsv} DBC CSV, {DbcDirect} DBC direct, hasSql={HasSql}, hasMap={HasMap}, hasMpq={HasMpq} [trace {TraceId}]",
            patch.Key, patch.Level, stackId, dbcCsvFiles.Count, dbcBinaryFiles.Count, hasSql, hasMap, hasMpq, result.CorrelationId);
        if (result.CorrelationId is not null)
        {
            AddLog(result, $"Trace id: {result.CorrelationId}");
        }

        if (!hasDbc && !hasSql && !hasMap && !hasMpq && !hasMpqRemovals)
        {
            AddLog(result, "Patch has no SQL, DBC, map or MPQ content; only recording the applied level.");
        }

        try
        {
            // 1) Ensure the database is up and healthy (only when there is SQL to run).
            if (needsDatabase)
            {
                await RunStageAsync("database-up", result, async () =>
                {
                    AddLog(result, "Starting database...");
                    await RunComposeAsync(stackId, "up -d ac-database", repoPath, cancellationToken);
                    await WaitForDatabaseAsync(stackId, stack.StackName, stack.DatabaseRootPassword, result, cancellationToken);
                }, cancellationToken);
            }

            // 2) Apply all standard AzerothCore SQL/updates first (ac-db-import) so the core updater
            //    never overwrites the custom patch SQL we are about to apply on top of it.
            if (hasSql)
            {
                await RunStageAsync("standard-updates", result,
                    () => EnsureStandardUpdatesAppliedAsync(stackId, stack.StackName, repoPath, result, cancellationToken), cancellationToken);
            }

            // 3) Stop world/auth so we can mutate server data safely (only when SQL/DBC/maps change it).
            if (needsServerStop)
            {
                await RunStageAsync("stop-servers", result, async () =>
                {
                    AddLog(result, "Stopping world and auth servers...");
                    await RunComposeAsync(stackId, "stop ac-worldserver ac-authserver", repoPath, cancellationToken);
                }, cancellationToken);
            }

            // 4) DBC pipeline. CSV/.txt sources are compiled onto a fresh export of the live worldserver
            //    DBCs; pre-built .dbc uploads are copied straight into the baseline. Then all DBCs are
            //    packaged into the client patch-D.MPQ. The updated DBCs are pushed to the server volume in
            //    the push-dbc stage below.
            var updatedDbc = new List<string>();
            if (hasDbcCsv)
            {
                await RunStageAsync("extract-dbc", result, async () =>
                {
                    AddLog(result, "Extracting latest DBC files from the live worldserver data volume...");
                    await ExtractServerDbcFromVolumeAsync(stack, stackRoot, cancellationToken);
                }, cancellationToken);

                await RunStageAsync("dbc-compile", result,
                    async () => updatedDbc = await ApplyDbcAsync(stack, stackRoot, patch.Key, dbcCsvFiles, result, cancellationToken), cancellationToken);
            }

            // Direct .dbc uploads take final precedence over any compiled result for the same file.
            if (hasDbcDirect)
            {
                await RunStageAsync("place-dbc", result, () =>
                {
                    var placed = PlaceDirectDbc(stackRoot, dbcBinaryFiles, result);
                    updatedDbc = updatedDbc.Concat(placed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    return Task.CompletedTask;
                }, cancellationToken);
            }

            if (hasDbc)
            {
                await RunStageAsync("build-patch-d", result,
                    () => BuildPatchDAsync(stack, stackRoot, result, cancellationToken), cancellationToken);
            }

            // 5) Override the server's DBCs with the freshly compiled set (same content that went into
            //    patch-D.MPQ), so the server and clients stay in sync.
            if (hasDbc && updatedDbc.Count > 0)
            {
                await RunStageAsync("push-dbc", result,
                    () => PushServerDbcToVolumeAsync(stack, stackRoot, updatedDbc, result, cancellationToken), cancellationToken);
            }

            // 6) Override maps in the data volume.
            if (hasMap)
            {
                await RunStageAsync("maps", result,
                    () => ApplyMapsAsync(stack, stackRoot, patch.Key, result, cancellationToken), cancellationToken);
            }

            // 6.5) Build MPQ archives from raw content (mpq.json manifest)
            var manifest = ReadMpqManifest(stackRoot, patch.Key);
            if (manifest is not null && manifest.Add.Count > 0)
            {
                await RunStageAsync("mpq-build", result, async () =>
                {
                    foreach (var mpqName in manifest.Add)
                    {
                        var mpqPath = Path.Combine(MigrationLayout.MpqDir(stackRoot, patch.Key), mpqName);
                        if (File.Exists(mpqPath))
                        {
                            AddLog(result, $"Pre-built {mpqName} found; skipping construction.");
                            continue;
                        }
                        await BuildMpqFromContentAsync(stack, stackRoot, patch.Key, mpqName, result, cancellationToken);
                    }
                    if (manifest.Remove.Count > 0)
                    {
                        mpqRemovals = mpqRemovals.Concat(manifest.Remove).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }, cancellationToken);

                // Re-check since construction may have produced new .mpq files and merged manifest removals
                hasMpq = PatchHasMpq(stackRoot, patch.Key);
                hasMpqRemovals = mpqRemovals.Count > 0;
            }

            // 7) Update the client overlay: first REMOVE any MPQs this patch retires, then publish the
            //    patch's own uploaded MPQ files. Removal-before-publish lets a patch replace an archive
            //    (drop old-name, add new-name) and guarantees retired content is gone before new content
            //    reaches clients.
            if (hasMpq || hasMpqRemovals)
            {
                await RunStageAsync("mpq", result,
                    () => PublishMpqAsync(stack, stackRoot, patch.Key, mpqRemovals, result, cancellationToken), cancellationToken);
            }

            // 8) Rescan the client-server container if client content changed (patch-D, uploaded MPQs,
            //    or removed MPQs), so its manifest version bumps and players are prompted to update.
            if (hasDbc || hasMpq || hasMpqRemovals)
            {
                await RunStageAsync("rescan", result,
                    () => RescanClientContainerAsync(stack, result, cancellationToken), cancellationToken);
            }

            // 9) Run the patch's SQL LAST, once every other stage (DBC, maps, MPQ, rescan) has succeeded.
            //    SQL is the most failure-prone step (e.g. duplicate keys in third-party content), so
            //    keeping it final means a SQL error never leaves the client/data work half-applied — all
            //    of it is already done and only the SQL needs fixing/re-running.
            if (hasSql)
            {
                await RunStageAsync("sql", result,
                    () => ApplySqlAsync(stackId, stack.StackName, stackRoot, patch.Key, stack.DatabaseRootPassword, result, cancellationToken), cancellationToken);
            }

            // 11) Persist the applied level.
            await RunStageAsync("persist-level", result, async () =>
            {
                PersistAppliedLevel(stack, patch.Key, patch.Level);
                await _dbContext.SaveChangesAsync(cancellationToken);
                AddLog(result, $"Recorded applied patch level {patch.Level}.");

                var metadata = await _individualProgression.ReadPatchMetadataAsync(stackRoot, patch.Key);
                if (metadata is not null)
                {
                    await _individualProgression.OnPatchAppliedAsync(stackId, metadata, result.Log, cancellationToken);
                }
            }, cancellationToken);

            // 12) Restart the stack (bring world/auth back up) only if we stopped them.
            if (needsServerStop)
            {
                await RunStageAsync("restart", result, async () =>
                {
                    AddLog(result, "Restarting stack...");
                    await RunComposeAsync(stackId, "up -d", repoPath, cancellationToken);
                }, cancellationToken);
            }

            overallStopwatch.Stop();
            result.Success = true;
            AddLog(result, "Patch applied successfully.");
            activity?.SetTag("apply.elapsed_ms", overallStopwatch.ElapsedMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.LogInformation(
                "Applied patch {PatchKey} to stack {StackId} successfully in {ElapsedMs} ms",
                patch.Key, stackId, overallStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            overallStopwatch.Stop();
            activity?.SetTag("apply.elapsed_ms", overallStopwatch.ElapsedMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Failed to apply patch {PatchKey} to stack {StackId} after {ElapsedMs} ms",
                patch.Key, stackId, overallStopwatch.ElapsedMilliseconds);
            result.Success = false;
            result.Error = ex.Message;
            AddLog(result, $"ERROR: {ex.Message}");

            // Best-effort: bring the stack back up so a failed apply does not leave it stopped.
            await RestartAfterFailureAsync(stackId, repoPath, result);
        }

        return result;
    }

    /// <summary>Best-effort stack restart after a failed apply/reapply, in its own trace span.</summary>
    private async Task RestartAfterFailureAsync(string stackId, string repoPath, ApplyPatchResultDto result)
    {
        using var activity = MigrationTelemetry.ActivitySource.StartActivity("migration.restart-after-failure", ActivityKind.Internal);
        activity?.SetTag("stack.id", stackId);
        try
        {
            await RunComposeAsync(stackId, "up -d", repoPath, CancellationToken.None);
            AddLog(result, "Stack restarted after failure.");
            _logger.LogInformation("Restarted stack {StackId} after patch failure", stackId);
        }
        catch (Exception restartEx)
        {
            activity?.SetStatus(ActivityStatusCode.Error, restartEx.Message);
            _logger.LogError(restartEx, "Failed to restart stack {StackId} after patch failure", stackId);
        }
    }

    /// <summary>
    /// Runs a single apply stage inside its own trace span with start/stop + timing logs, so a
    /// failed apply pinpoints exactly which stage broke. Rethrows so the caller handles recovery.
    /// </summary>
    private async Task RunStageAsync(string stage, ApplyPatchResultDto result, Func<Task> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = MigrationTelemetry.ActivitySource.StartActivity($"migration.stage.{stage}", ActivityKind.Internal);
        activity?.SetTag("stage.name", stage);
        var stopwatch = Stopwatch.StartNew();
        _applyProgress?.Stage(stage);
        _logger.LogInformation("Patch stage started: {Stage}", stage);

        try
        {
            await action();
            stopwatch.Stop();
            activity?.SetTag("stage.elapsed_ms", stopwatch.ElapsedMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.LogInformation("Patch stage completed: {Stage} ({ElapsedMs} ms)", stage, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            activity?.SetTag("stage.elapsed_ms", stopwatch.ElapsedMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Patch stage failed: {Stage} ({ElapsedMs} ms)", stage, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ApplyPatchResultDto> ApplyStandardDbUpdatesAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var repoPath = RepoPath(stackId);
        await ResolveEngineContextAsync(stack, cancellationToken);

        var result = new ApplyPatchResultDto { PatchKey = "db-updates" };
        if (!Directory.Exists(repoPath))
        {
            result.Success = false;
            result.Error = $"Stack build directory not found: {repoPath}";
            return result;
        }

        try
        {
            await RunStageAsync("database-up", result, async () =>
            {
                AddLog(result, "Starting database...");
                await RunComposeAsync(stackId, "up -d ac-database", repoPath, cancellationToken);
                await WaitForDatabaseAsync(stackId, stack.StackName, stack.DatabaseRootPassword, result, cancellationToken);
            }, cancellationToken);

            await RunStageAsync("standard-updates", result,
                () => EnsureStandardUpdatesAppliedAsync(stackId, stack.StackName, repoPath, result, cancellationToken), cancellationToken);

            result.Success = true;
            AddLog(result, "Standard database updates applied.");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            AddLog(result, $"ERROR: {ex.Message}");
        }

        return result;
    }

    public async Task<ApplyPatchResultDto> ReapplyAllAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        await ResolveEngineContextAsync(stack, cancellationToken);

        var result = new ApplyPatchResultDto { PatchKey = "*", Level = stack.AppliedPatchLevel };

        using var activity = MigrationTelemetry.ActivitySource.StartActivity("migration.reapply-all", ActivityKind.Internal);
        activity?.SetTag("stack.id", stackId);
        activity?.SetTag("current.level", stack.AppliedPatchLevel);
        result.CorrelationId = activity?.TraceId.ToString();

        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["StackId"] = stackId,
            ["CorrelationId"] = result.CorrelationId,
            ["Operation"] = "ReapplyAll"
        });

        var appliedPatches = EnumeratePatches(stackRoot)
            .Where(p => p.Level <= stack.AppliedPatchLevel)
            .OrderBy(p => p.Level)
            .ToList();

        activity?.SetTag("reapply.patch_count", appliedPatches.Count);

        if (appliedPatches.Count == 0)
        {
            result.Success = true;
            AddLog(result, "No applied patches to reapply.");
            _logger.LogInformation("Reapply-all requested for stack {StackId} but no patches are applied", stackId);
            return result;
        }

        var (applyAllowed, applyBlockedReason) = await _individualProgression.CheckPatchApplyAllowedAsync(stackId, cancellationToken);
        if (!applyAllowed)
        {
            result.Success = false;
            result.Error = applyBlockedReason;
            activity?.SetStatus(ActivityStatusCode.Error, result.Error);
            _logger.LogWarning("Rejected reapply-all on stack {StackId}: {Reason}", stackId, result.Error);
            return result;
        }

        var repoPath = RepoPath(stackId);
        if (!Directory.Exists(repoPath))
        {
            result.Success = false;
            result.Error = $"Stack build directory not found: {repoPath}";
            activity?.SetStatus(ActivityStatusCode.Error, result.Error);
            _logger.LogWarning("Reapply-all rejected for stack {StackId}: {Reason}", stackId, result.Error);
            return result;
        }

        // Classify each applied patch's content up front so we know which pipelines to run.
        var plans = appliedPatches.Select(patch =>
        {
            var dbcSources = EnumerateDbcSourceFiles(MigrationLayout.DbcDir(stackRoot, patch.Key)).OrderBy(p => p).ToList();
            return new
            {
                Patch = patch,
                DbcCsv = dbcSources.Where(IsDbcCsvSource).ToList(),
                DbcBinary = dbcSources.Where(IsDbcBinary).ToList(),
                HasSql = PatchHasSql(stackRoot, patch.Key),
                HasMap = PatchHasMap(stackRoot, patch.Key),
                HasMpq = PatchHasMpq(stackRoot, patch.Key),
                MpqRemovals = ReadMpqRemovals(stackRoot, patch.Key)
            };
        }).ToList();

        var anyDbcCsv = plans.Any(p => p.DbcCsv.Count > 0);
        var anyDbc = plans.Any(p => p.DbcCsv.Count > 0 || p.DbcBinary.Count > 0);
        var anyMpqManifest = appliedPatches.Any(p => ReadMpqManifest(stackRoot, p.Key) is { Add.Count: > 0 } or { Remove.Count: > 0 });
        var anyClientContent = anyDbc || anyMpqManifest || plans.Any(p => p.HasMpq || p.MpqRemovals.Count > 0);

        if (anyDbcCsv && !IsBaselineInitialized(stackRoot))
        {
            // ExtractServerDbcFromVolumeAsync will populate the baseline from the live volume, but a CSV
            // compile still needs a baseline present; the extract below provides it. (No hard fail here.)
            AddLog(result, "No DBC baseline captured yet; it will be re-extracted from the running stack.");
        }

        var overallStopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "Reapplying {Count} patch(es) to stack {StackId} [trace {TraceId}]",
            appliedPatches.Count, stackId, result.CorrelationId);
        if (result.CorrelationId is not null)
        {
            AddLog(result, $"Trace id: {result.CorrelationId}");
        }

        try
        {
            await RunStageAsync("database-up", result, async () =>
            {
                AddLog(result, "Starting database...");
                await RunComposeAsync(stackId, "up -d ac-database", repoPath, cancellationToken);
                await WaitForDatabaseAsync(stackId, stack.StackName, stack.DatabaseRootPassword, result, cancellationToken);
            }, cancellationToken);

            // Apply standard AzerothCore updates first, so custom SQL is layered on top.
            await RunStageAsync("standard-updates", result,
                () => EnsureStandardUpdatesAppliedAsync(stackId, stack.StackName, repoPath, result, cancellationToken), cancellationToken);

            await RunStageAsync("stop-servers", result, async () =>
            {
                AddLog(result, "Stopping world and auth servers...");
                await RunComposeAsync(stackId, "stop ac-worldserver ac-authserver", repoPath, cancellationToken);
            }, cancellationToken);

            // 1) Fetch the DBC set from the server ONCE (fresh baseline the CSVs are compiled onto).
            if (anyDbcCsv)
            {
                await RunStageAsync("extract-dbc", result, async () =>
                {
                    AddLog(result, "Extracting the current DBC set from the running stack's data volume...");
                    await ExtractServerDbcFromVolumeAsync(stack, stackRoot, cancellationToken);
                }, cancellationToken);
            }

            // 2) Walk each applied patch in order: compile its DBC CSVs onto the cumulative baseline,
            //    stage its direct .dbc uploads, apply its SQL, and re-apply its maps. patch-D and the
            //    MPQ overlay are built/published ONCE after the loop from the accumulated results.
            var updatedDbc = new List<string>();
            foreach (var plan in plans)
            {
                var patch = plan.Patch;

                if (plan.DbcCsv.Count > 0)
                {
                    await RunStageAsync($"dbc-compile:{patch.Key}", result, async () =>
                    {
                        AddLog(result, $"Compiling {plan.DbcCsv.Count} DBC CSV(s) for {patch.Key}...");
                        var compiled = await ApplyDbcAsync(stack, stackRoot, patch.Key, plan.DbcCsv, result, cancellationToken);
                        updatedDbc.AddRange(compiled);
                    }, cancellationToken);
                }

                if (plan.DbcBinary.Count > 0)
                {
                    await RunStageAsync($"place-dbc:{patch.Key}", result, () =>
                    {
                        var placed = PlaceDirectDbc(stackRoot, plan.DbcBinary, result);
                        updatedDbc.AddRange(placed);
                        return Task.CompletedTask;
                    }, cancellationToken);
                }

                if (plan.HasSql)
                {
                    await RunStageAsync($"sql:{patch.Key}", result, async () =>
                    {
                        AddLog(result, $"Reapplying SQL for {patch.Key}...");
                        await ApplySqlAsync(stackId, stack.StackName, stackRoot, patch.Key, stack.DatabaseRootPassword, result, cancellationToken);
                    }, cancellationToken);
                }

                if (plan.HasMap)
                {
                    await RunStageAsync($"maps:{patch.Key}", result,
                        () => ApplyMapsAsync(stack, stackRoot, patch.Key, result, cancellationToken), cancellationToken);
                }
            }

            updatedDbc = updatedDbc.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // 3) Build the client patch-D.MPQ from the final cumulative DBC set (once).
            if (anyDbc)
            {
                await RunStageAsync("build-patch-d", result,
                    () => BuildPatchDAsync(stack, stackRoot, result, cancellationToken), cancellationToken);
            }

            // 4) Push the changed DBCs to the server data volume so server and clients stay in sync.
            if (updatedDbc.Count > 0)
            {
                await RunStageAsync("push-dbc", result,
                    () => PushServerDbcToVolumeAsync(stack, stackRoot, updatedDbc, result, cancellationToken), cancellationToken);
            }

            // 5a) Build MPQ archives from raw content (mpq.json manifests) before publishing.
            //     Uses the full construction plan so MPQs removed by later patches are skipped.
            var constructionPlan = ResolveMpqConstructionPlan(stackRoot, appliedPatches);
            if (constructionPlan.ToBuild.Count > 0)
            {
                await RunStageAsync("mpq-build", result, async () =>
                {
                    foreach (var skipped in constructionPlan.Skipped)
                    {
                        AddLog(result, $"Skipping MPQ '{skipped}' (removed by a later patch).");
                    }

                    foreach (var entry in constructionPlan.ToBuild)
                    {
                        if (entry.PreBuilt)
                        {
                            AddLog(result, $"Pre-built {entry.MpqName} found in {entry.PatchKey}; skipping construction.");
                            continue;
                        }
                        await BuildMpqFromContentAsync(stack, stackRoot, entry.PatchKey, entry.MpqName, result, cancellationToken);
                    }
                }, cancellationToken);
            }

            // 5b) Publish each patch's MPQ overlay changes in order (removals first, then uploads), so the
            //     final overlay reflects every applied patch with later patches overriding earlier ones.
            foreach (var plan in plans)
            {
                var patchMpqRemovals = plan.MpqRemovals;
                var patchManifest = ReadMpqManifest(stackRoot, plan.Patch.Key);
                if (patchManifest?.Remove.Count > 0)
                {
                    patchMpqRemovals = patchMpqRemovals.Concat(patchManifest.Remove)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }

                var patchHasMpq = PatchHasMpq(stackRoot, plan.Patch.Key);
                if (patchHasMpq || patchMpqRemovals.Count > 0)
                {
                    await RunStageAsync($"mpq:{plan.Patch.Key}", result,
                        () => PublishMpqAsync(stack, stackRoot, plan.Patch.Key, patchMpqRemovals, result, cancellationToken), cancellationToken);
                }
            }

            // 6) Restart the stack so the worldserver reloads the pushed DBCs and the client-server is up.
            await RunStageAsync("restart", result, async () =>
            {
                AddLog(result, "Restarting stack...");
                await RunComposeAsync(stackId, "up -d", repoPath, cancellationToken);
            }, cancellationToken);

            // 7) Rescan the client-server manifest LAST (once the container is up) so clients are pinged
            //    to pull the rebuilt patch-D and republished MPQ overlay.
            if (anyClientContent)
            {
                await RunStageAsync("rescan", result,
                    () => RescanClientContainerAsync(stack, result, cancellationToken), cancellationToken);
            }

            overallStopwatch.Stop();
            result.Success = true;
            AddLog(result, "All applied patches reapplied successfully (SQL, DBC, maps and MPQ).");
            activity?.SetTag("reapply.elapsed_ms", overallStopwatch.ElapsedMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _logger.LogInformation(
                "Reapplied all patches for stack {StackId} successfully in {ElapsedMs} ms", stackId, overallStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            overallStopwatch.Stop();
            activity?.SetTag("reapply.elapsed_ms", overallStopwatch.ElapsedMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Failed to reapply patches for stack {StackId} after {ElapsedMs} ms", stackId, overallStopwatch.ElapsedMilliseconds);
            result.Success = false;
            result.Error = ex.Message;
            AddLog(result, $"ERROR: {ex.Message}");

            await RestartAfterFailureAsync(stackId, repoPath, result);
        }

        return result;
    }

    /// <summary>
    /// Runs the one-shot <c>ac-db-import</c> service to completion so all standard AzerothCore base
    /// and update SQL is applied before any custom patch SQL. Idempotent: on an already-updated DB
    /// it applies nothing and exits 0.
    /// </summary>
    private async Task EnsureStandardUpdatesAppliedAsync(
        string stackId, string? stackName, string repoPath, ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        AddLog(result, "Applying standard AzerothCore database updates (ac-db-import)...");
        await RunComposeAsync(stackId, "up -d ac-db-import", repoPath, cancellationToken);
        await WaitForContainerExitAsync(DbImportContainer(stackId, stackName), "ac-db-import", result, cancellationToken);
    }

    private static bool PatchHasSql(string stackRoot, string patchKey)
    {
        foreach (var database in MigrationLayout.SqlDatabases.Keys)
        {
            var dir = MigrationLayout.SqlDatabaseDir(stackRoot, patchKey, database);
            if (EnumerateCategoryFiles(dir, $"sql/{database}").Any())
            {
                return true;
            }
        }

        return false;
    }

    private async Task WaitForContainerExitAsync(
        string container, string displayName, ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(30);
        var inspectArgs = $"{_engineContextArg}inspect -f \"{{{{.State.Status}}}}|{{{{.State.ExitCode}}}}\" " + container;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inspect = await RunProcessAsync("docker", inspectArgs, workingDirectory: null, stdin: null, cancellationToken);
            if (inspect.ExitCode == 0)
            {
                var parts = inspect.StdOut.Trim().Split('|');
                var status = parts.Length > 0 ? parts[0].Trim() : string.Empty;

                if (status.Equals("exited", StringComparison.OrdinalIgnoreCase))
                {
                    var code = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var parsed) ? parsed : -1;
                    if (code != 0)
                    {
                        throw new InvalidOperationException($"{displayName} failed with exit code {code}.");
                    }

                    AddLog(result, "Standard database updates applied.");
                    return;
                }
            }

            await Task.Delay(3000, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for {displayName} to complete.");
    }

    private async Task ApplySqlAsync(
        string stackId, string? stackName, string stackRoot, string patchKey, string rootPassword,
        ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        var container = DbContainer(stackId, stackName);

        foreach (var (subfolder, database) in MigrationLayout.SqlDatabases)
        {
            var dir = MigrationLayout.SqlDatabaseDir(stackRoot, patchKey, subfolder);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            // Include SQL at the root and inside one level of container sub-folders, ordered by
            // relative path so ordering is deterministic across containers.
            var sqlFiles = EnumerateCategoryFiles(dir, $"sql/{subfolder}")
                .OrderBy(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), StringComparer.Ordinal)
                .ToList();
            foreach (var sqlFile in sqlFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeName = Path.GetRelativePath(dir, sqlFile).Replace('\\', '/');
                AddLog(result, $"Applying {relativeName} -> {database} (transactional)...");

                // Wrap the file in a single transaction and let the mysql client abort on the first
                // error (default in batch mode). If any statement fails, mysql exits non-zero before
                // reaching COMMIT and the connection closes with the transaction uncommitted, so
                // InnoDB rolls back the whole file. NOTE: DDL (CREATE/ALTER/DROP) auto-commits in
                // MySQL and cannot be rolled back — this protects the DML that AC patch SQL uses.
                await using var stream = OpenTransactionalSqlStream(sqlFile);
                // --disable-local-infile prevents operator SQL from pulling files off the manager host
                // via LOAD DATA LOCAL INFILE (server-side is also blocked by local-infile=0).
                var args = $"{_engineContextArg}exec -i {container} mysql --disable-local-infile -uroot -p{rootPassword} {database}";
                var run = await RunProcessAsync("docker", args, workingDirectory: null, stdin: stream, cancellationToken);

                if (run.ExitCode != 0)
                {
                    var error = FilterMySqlWarnings(run.StdErr);
                    throw new InvalidOperationException(
                        $"SQL '{relativeName}' failed against {database} (rolled back): {error}");
                }
            }
        }
    }

    /// <summary>
    /// Compiles the patch's CSVs onto the extracted baseline DBCs (promoting each result back into
    /// <c>server_dbc/</c>) and returns the names of the DBCs that were updated. The updated DBCs are
    /// NOT pushed to the live data volume here — that override runs after the SQL stage.
    /// </summary>
    private async Task<List<string>> ApplyDbcAsync(
        Data.Entities.ManagedStackEntity stack, string stackRoot, string patchKey, List<string> dbcTxtFiles,
        ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        var serverDbcDir = MigrationLayout.ServerDbcDir(stackRoot);

        AddLog(result, "Ensuring the WDBX editor image is available (built once, then cached)...");
        await _imageService.EnsureWdbxImageAsync(cancellationToken);

        // Temp work dir under the stack root; seeded into a throwaway work volume for the tool run.
        var workDir = Path.Combine(stackRoot, ".migration-tmp", $"{patchKey}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var updatedDbc = new List<string>();
            // Track each DBC's outcome; a non-zero exit means the CSV did not fully import (the
            // WDBXEditor CLI now returns non-zero on any ImportCSV failure). We attempt every DBC so
            // the operator sees the full picture, then fail the whole apply if ANY did not succeed.
            var failures = new List<string>();

            foreach (var txtPath in dbcTxtFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dbcName = Path.GetFileNameWithoutExtension(txtPath) + ".dbc";
                var csvName = Path.GetFileName(txtPath);
                var baselineDbc = Path.Combine(serverDbcDir, dbcName);
                if (!File.Exists(baselineDbc))
                {
                    var missing = $"No baseline DBC for '{csvName}' (expected server_dbc/{dbcName}).";
                    AddLog(result, $"  FAILED {dbcName}: {missing}");
                    failures.Add(missing);
                    continue;
                }

                // Stage the baseline .dbc and a CRLF-normalized copy of the CSV in the work dir.
                var workDbc = Path.Combine(workDir, dbcName);
                var workCsv = Path.Combine(workDir, csvName);

                File.Copy(baselineDbc, workDbc, overwrite: true);
                await NormalizeToCrlfAsync(txtPath, workCsv, cancellationToken);

                AddLog(result, $"Importing {csvName} into {dbcName} (WDBXEditor)...");

                var toolArgs =
                    $"-import -f \"{dbcName}\" -b {_migrationOptions.WoWBuild} -c \"{csvName}\" -h true -u Update -i FixIds";

                var run = await _remoteEngine.RunToolWithWorkVolumeAsync(
                    stack, workDir, _migrationOptions.WdbxImage, toolArgs, cancellationToken);
                var importOutput = string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr;
                if (run.ExitCode != 0)
                {
                    AddLog(result, $"  FAILED {dbcName}: {importOutput?.Trim()}");
                    failures.Add($"{csvName} -> {dbcName}: {importOutput?.Trim()}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(run.StdOut))
                {
                    AddLog(result, $"  {run.StdOut.Trim()}");
                }

                // The import overwrote workDbc in place; promote it back into the cumulative baseline.
                File.Copy(workDbc, baselineDbc, overwrite: true);
                updatedDbc.Add(dbcName);
            }

            // If any DBC didn't fully import, abort the apply before touching the live data volume so
            // players never receive a partially-updated DBC set.
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{failures.Count} of {dbcTxtFiles.Count} DBC import(s) failed; aborting before publishing:\n  - " +
                    string.Join("\n  - ", failures));
            }

            return updatedDbc;
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>
    /// Copies pre-built <c>.dbc</c> uploads straight into the cumulative <c>server_dbc/</c> baseline
    /// (overwriting same-named files) and returns their names, so they're packaged into patch-D.MPQ and
    /// pushed to the server without any export/compile step. Creates the baseline dir if needed, so a
    /// direct-DBC-only patch works even when no baseline was ever captured.
    /// </summary>
    private List<string> PlaceDirectDbc(string stackRoot, IReadOnlyList<string> dbcFiles, ApplyPatchResultDto result)
    {
        var serverDbcDir = MigrationLayout.ServerDbcDir(stackRoot);
        Directory.CreateDirectory(serverDbcDir);

        var names = new List<string>();
        foreach (var src in dbcFiles)
        {
            // Flatten any container sub-folder: a DBC's identity is just its file name.
            var name = Path.GetFileName(src);
            File.Copy(src, Path.Combine(serverDbcDir, name), overwrite: true);
            names.Add(name);
        }

        AddLog(result, $"Staged {names.Count} uploaded DBC file(s) directly into the server baseline (no CSV compile).");
        return names;
    }

    /// <summary>
    /// Overrides the stack's live DBCs (/data/dbc) with the compiled results from <c>server_dbc/</c>.
    /// Runs after the SQL stage so the server DBCs are only replaced once the whole apply has reached
    /// the publishing phase, keeping the server and the patch-D.MPQ clients in sync.
    /// </summary>
    private async Task PushServerDbcToVolumeAsync(
        Data.Entities.ManagedStackEntity stack, string stackRoot, IReadOnlyList<string> dbcNames,
        ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        if (dbcNames.Count == 0)
        {
            return;
        }

        var serverDbcDir = MigrationLayout.ServerDbcDir(stackRoot);
        AddLog(result, $"Overriding {dbcNames.Count} DBC file(s) on the server (data volume)...");

        // Stage the changed DBCs under a `dbc/` dir and stream them into the stack's data volume (seeded
        // at the volume root, so they land under /data/dbc), overwriting in place. Only the DBCs we
        // updated are pushed (server_dbc holds the full extracted set; we override just the changed ones).
        await SeedDataVolumeSubdirAsync(
            stack, stackRoot, "dbc", dbcNames.Select(n => Path.Combine(serverDbcDir, n)), cancellationToken);
    }

    /// <summary>
    /// Streams a set of files into a subfolder of the stack's data volume by staging them under
    /// <c>{subdir}/</c> in a temp dir and seeding the volume (files land at <c>/data/{subdir}/...</c>,
    /// overwriting in place). Used to push DBC/map overrides for both local and external stacks (no host
    /// bind mount required, so it works whether the manager's data is a bind mount or a named volume).
    /// </summary>
    private async Task SeedDataVolumeSubdirAsync(
        Data.Entities.ManagedStackEntity stack, string stackRoot, string subdir,
        IEnumerable<string> sourceFiles, CancellationToken cancellationToken)
    {
        var seedRoot = Path.Combine(stackRoot, ".migration-tmp", $"volseed-{Guid.NewGuid():N}");
        var stageDir = Path.Combine(seedRoot, subdir);
        Directory.CreateDirectory(stageDir);

        try
        {
            foreach (var source in sourceFiles)
            {
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(stageDir, Path.GetFileName(source)), overwrite: true);
                }
            }

            await _remoteEngine.SeedVolumeAsync(stack, DataVolumeName(stack.Id), seedRoot, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(seedRoot);
        }
    }

    private async Task ApplyMapsAsync(
        Data.Entities.ManagedStackEntity stack, string stackRoot, string patchKey,
        ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        var mapDir = MigrationLayout.MapDir(stackRoot, patchKey);
        if (!Directory.Exists(mapDir) || !Directory.EnumerateFileSystemEntries(mapDir).Any())
        {
            return;
        }

        AddLog(result, "Overriding maps in the data volume...");

        // Flatten the (possibly nested) map files and stream them into the stack's data volume under
        // /data/maps, overwriting in place. Map files must land directly under /data/maps regardless of
        // how they're organized in the patch (containers are only an organizational convenience).
        var mapFiles = Directory.EnumerateFiles(mapDir, "*", SearchOption.AllDirectories);
        await SeedDataVolumeSubdirAsync(stack, stackRoot, "maps", mapFiles, cancellationToken);
    }

    private async Task PublishMpqAsync(
        Data.Entities.ManagedStackEntity stack, string stackRoot, string patchKey,
        IReadOnlyList<string> mpqRemovals, ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        var mpqDir = MigrationLayout.MpqDir(stackRoot, patchKey);
        // Enumerate case-insensitively: WoW archives are commonly upper-case ".MPQ", and a glob like
        // "*.mpq" misses those on case-sensitive filesystems (Linux/Docker), silently skipping the
        // upload at apply time even though the UI accepted it.
        var mpqFiles = EnumerateMpqFiles(mpqDir).ToList();

        // The stack's client-server overlay (Data/), the read-write layer the container serves as
        // Managed content over the shared read-only base client.
        var overlayDataDir = MigrationLayout.ClientOverlayDataDir(stackRoot);
        Directory.CreateDirectory(overlayDataDir);

        // 1) Remove retired MPQs FIRST, so they are gone from the overlay before any new archive lands.
        //    A patch that both removes "foo.mpq" and re-publishes it will therefore end up with the new
        //    copy (removal happens, then the publish below re-adds it).
        // Volume-relative paths (under the overlay root) to purge from the engine's overlay volume. The
        // local delete alone is not enough: the overlay push is additive (overwrites, never purges), so
        // the file must be deleted from the volume explicitly or it lingers and stays in the manifest.
        var volumePathsToDelete = new List<string>();
        foreach (var name in mpqRemovals)
        {
            var fileName = Path.GetFileName(name);
            var target = Path.Combine(overlayDataDir, fileName);
            // Keep the delete inside the overlay dir even if a crafted name slipped past sanitization.
            if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(overlayDataDir), StringComparison.Ordinal))
            {
                continue;
            }

            // Always queue the volume-side delete (the file may exist in the volume even if the local
            // overlay mirror is missing it, e.g. after a manager restart or partial prior run).
            volumePathsToDelete.Add($"Data/{fileName}");

            if (File.Exists(target))
            {
                File.Delete(target);
                AddLog(result, $"Removed published MPQ: {fileName}");
            }
            else
            {
                AddLog(result, $"Published MPQ not in local overlay; will still purge it from the engine volume: {fileName}");
            }
        }

        // Purge the retired archives from the engine's overlay volume (before publishing new ones).
        if (volumePathsToDelete.Count > 0)
        {
            await _remoteEngine.DeleteVolumePathsAsync(
                stack, DockerComposeOverrideGenerator.ClientOverlayVolumeName(stack.Id), volumePathsToDelete, cancellationToken);
        }

        // 2) Publish this patch's uploaded MPQs. Always overwrite a same-named MPQ so an updated patch
        //    archive replaces the previous one instead of being ignored.
        foreach (var mpq in mpqFiles)
        {
            var dest = Path.Combine(overlayDataDir, Path.GetFileName(mpq));
            File.Copy(mpq, dest, overwrite: true);
        }

        if (mpqFiles.Count == 0)
        {
            // Removals (if any) were purged from the volume directly above; there is nothing new to
            // publish, so skip the (potentially slow, additive) overlay push.
            return;
        }

        AddLog(result, $"Published {mpqFiles.Count} MPQ file(s) to the client overlay (overwriting same-named files).");
        await PushOverlayToEngineAsync(stack, stackRoot, result, cancellationToken);
    }

    /// <summary>
    /// Builds an MPQ file from raw content in a patch's mpq directory using the mpqtool.
    /// Raw content is any non-.mpq file or directory in the mpq directory (the content tree
    /// forms the internal MPQ path structure).
    /// </summary>
    private async Task BuildMpqFromContentAsync(
        Data.Entities.ManagedStackEntity stack, string stackRoot, string patchKey,
        string mpqName, ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        var mpqDir = MigrationLayout.MpqDir(stackRoot, patchKey);

        var contentDirName = Path.GetFileNameWithoutExtension(mpqName);
        var contentDir = Path.Combine(mpqDir, contentDirName);

        if (!Directory.Exists(contentDir))
        {
            contentDir = mpqDir;
        }

        var workDir = Path.Combine(stackRoot, ".migration-tmp", $"mpqbuild-{Guid.NewGuid():N}");
        var stageDir = Path.Combine(workDir, contentDirName);
        Directory.CreateDirectory(stageDir);

        try
        {
            foreach (var file in Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".mpq" or ".json" or ".desc")
                    continue;

                var relativePath = Path.GetRelativePath(contentDir, file);
                var destPath = Path.Combine(stageDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file, destPath, overwrite: true);
            }

            if (!Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories).Any())
            {
                AddLog(result, $"No content files found for MPQ '{mpqName}' in {patchKey}; skipping construction.");
                return;
            }

            AddLog(result, $"Building {mpqName} from raw content in {patchKey}...");

            await _imageService.EnsureMpqToolImageAsync(cancellationToken);

            var toolArgs = $"\"{mpqName}\" \"{contentDirName}\"";
            var run = await _remoteEngine.RunToolWithWorkVolumeAsync(
                stack, workDir, _migrationOptions.MpqToolImage, toolArgs, cancellationToken);

            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"MPQ build failed for {mpqName}: {(string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr)}");
            }

            var producedMpq = Path.Combine(workDir, mpqName);
            if (!File.Exists(producedMpq))
            {
                throw new InvalidOperationException($"MPQ tool did not produce {mpqName}.");
            }

            File.Copy(producedMpq, Path.Combine(mpqDir, mpqName), overwrite: true);
            AddLog(result, $"Built {mpqName} successfully.");
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>Whether a patch contains any uploaded MPQ files (case-insensitive extension match).</summary>
    private static bool PatchHasMpq(string stackRoot, string patchKey) =>
        EnumerateMpqFiles(MigrationLayout.MpqDir(stackRoot, patchKey)).Any();

    /// <summary>Enumerates <c>.mpq</c> files (any case) directly in the patch's mpq folder.</summary>
    private static IEnumerable<string> EnumerateMpqFiles(string mpqDir) =>
        Directory.Exists(mpqDir)
            ? Directory.EnumerateFiles(mpqDir, "*", SearchOption.TopDirectoryOnly)
                .Where(p => Path.GetExtension(p).Equals(".mpq", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();

    /// <summary>Whether a patch contains any uploaded map files (root or one container level).</summary>
    private static bool PatchHasMap(string stackRoot, string patchKey)
    {
        var mapDir = MigrationLayout.MapDir(stackRoot, patchKey);
        return Directory.Exists(mapDir) && Directory.EnumerateFiles(mapDir, "*", SearchOption.AllDirectories).Any();
    }

    /// <summary>
    /// Pushes the whole local overlay directory into the stack's overlay volume so the client-server
    /// container serves the freshly published MPQs. Runs for every stack (a daemon-side copy locally, a
    /// tar stream over SSH for external), since the container mounts the overlay named volume rather than
    /// a host bind mount.
    /// </summary>
    private async Task PushOverlayToEngineAsync(
        Data.Entities.ManagedStackEntity stack, string stackRoot, ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        var overlayDir = MigrationLayout.ClientOverlayDir(stackRoot);
        if (!Directory.Exists(overlayDir))
        {
            return;
        }

        AddLog(result, "Publishing the client overlay to the stack's engine...");
        await _remoteEngine.SeedVolumeAsync(
            stack, DockerComposeOverrideGenerator.ClientOverlayVolumeName(stack.Id), overlayDir, cancellationToken);
    }

    /// <summary>
    /// Rescans the stack's client-server container so its manifest version bumps after content changes.
    /// Runs an authenticated <c>POST /rescan</c> from inside the container (via <c>docker exec curl</c>,
    /// context-aware for external stacks), which works regardless of manager-to-container networking.
    /// </summary>
    private async Task RescanClientContainerAsync(
        Data.Entities.ManagedStackEntity stack, ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        if (!stack.ClientEnabled)
        {
            AddLog(result, "Stack has no client-server container; skipping manifest rescan.");
            return;
        }

        AddLog(result, "Rescanning client-server manifest...");

        var container = $"{ContainerPrefix(stack.Id, stack.StackName)}-client";
        var token = stack.ArmorySessionSecret;
        var port = _clientServerOptions.ContainerPort;

        // Exec curl inside the container against its own loopback: no host networking assumptions, and
        // the client image already ships curl for its healthcheck. Build argv explicitly (no shell) so the
        // bearer header (which contains a space) and the URL survive intact — a single quoted/escaped
        // command string gets re-tokenized and mangled ("curl: (2) no URL specified").
        var args = new List<string>();
        if (!string.IsNullOrEmpty(_engineContext))
        {
            args.Add("--context");
            args.Add(_engineContext);
        }
        args.Add("exec");
        args.Add(container);
        args.Add("curl");
        args.Add("-fsS");
        args.Add("-X");
        args.Add("POST");
        if (!string.IsNullOrEmpty(token))
        {
            args.Add("-H");
            args.Add($"Authorization: Bearer {token}");
        }
        args.Add($"http://localhost:{port}/rescan");

        var run = await RunProcessAsync(
            "docker", arguments: string.Empty, workingDirectory: null, stdin: null, cancellationToken, argumentList: args);
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Client-server rescan failed: {(string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr)}");
        }

        AddLog(result, "Client-server manifest rescanned.");
    }

    /// <summary>
    /// Packages every DBC in <c>server_dbc/</c> into a client <c>patch-D.MPQ</c> under a
    /// <c>DBFilesClient/</c> tree and writes it into the stack's client overlay. Uses the prebuilt,
    /// cached MPQ sidecar image (ensured build-if-missing) so applies never recompile it.
    /// </summary>
    private async Task BuildPatchDAsync(
        Data.Entities.ManagedStackEntity stack, string stackRoot, ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        var serverDbcDir = MigrationLayout.ServerDbcDir(stackRoot);
        var dbcs = Directory.Exists(serverDbcDir)
            ? Directory.EnumerateFiles(serverDbcDir, "*.dbc").OrderBy(p => p, StringComparer.Ordinal).ToList()
            : new List<string>();

        if (dbcs.Count == 0)
        {
            AddLog(result, "No DBC files in the baseline; skipping patch-D build.");
            return;
        }

        AddLog(result, "Ensuring the MPQ tool image is available (built once, then cached)...");
        await _imageService.EnsureMpqToolImageAsync(cancellationToken);

        // Stage the DBCs under DBFilesClient/ in a work dir seeded into a throwaway work volume.
        var workDir = Path.Combine(stackRoot, ".migration-tmp", $"patchd-{Guid.NewGuid():N}");
        var dbFilesClientDir = Path.Combine(workDir, "DBFilesClient");
        Directory.CreateDirectory(dbFilesClientDir);

        try
        {
            foreach (var dbc in dbcs)
            {
                File.Copy(dbc, Path.Combine(dbFilesClientDir, Path.GetFileName(dbc)), overwrite: true);
            }

            var mpqName = _migrationOptions.PatchDMpqName;
            AddLog(result, $"Packaging {dbcs.Count} DBC file(s) into {mpqName} (DBFilesClient/)...");

            var toolArgs = $"\"{mpqName}\" DBFilesClient";
            var run = await _remoteEngine.RunToolWithWorkVolumeAsync(
                stack, workDir, _migrationOptions.MpqToolImage, toolArgs, cancellationToken);
            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"patch-D MPQ build failed: {(string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr)}");
            }

            var producedMpq = Path.Combine(workDir, mpqName);
            if (!File.Exists(producedMpq))
            {
                throw new InvalidOperationException($"MPQ tool did not produce {mpqName}.");
            }

            // patch-D.MPQ is Managed content: write it into the client overlay (Data/) the container
            // serves, then push to the remote engine for external stacks.
            var overlayDataDir = MigrationLayout.ClientOverlayDataDir(stackRoot);
            Directory.CreateDirectory(overlayDataDir);
            File.Copy(producedMpq, Path.Combine(overlayDataDir, mpqName), overwrite: true);
            AddLog(result, $"Wrote {mpqName} to the client overlay ({dbcs.Count} DBC file(s)).");
            await PushOverlayToEngineAsync(stack, stackRoot, result, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private void PersistAppliedLevel(Data.Entities.ManagedStackEntity stack, string patchKey, int level)
    {
        stack.AppliedPatchLevel = level;

        var records = new List<AppliedPatchRecord>();
        if (!string.IsNullOrWhiteSpace(stack.AppliedPatchesJson))
        {
            try
            {
                records = JsonSerializer.Deserialize<List<AppliedPatchRecord>>(stack.AppliedPatchesJson, JsonOptions)
                    ?? new List<AppliedPatchRecord>();
            }
            catch
            {
                records = new List<AppliedPatchRecord>();
            }
        }

        records.RemoveAll(r => string.Equals(r.Key, patchKey, StringComparison.OrdinalIgnoreCase));
        records.Add(new AppliedPatchRecord { Key = patchKey, Level = level, AppliedAt = DateTime.UtcNow });
        stack.AppliedPatchesJson = JsonSerializer.Serialize(records, JsonOptions);
    }

    // ===== Docker / process helpers =====

    private async Task RunComposeAsync(string stackId, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var (command, argPrefix) = DockerComposeHelper.GetDockerCompose(_dockerOptions.ComposeCommand);
        var project = ComposeProject(stackId);
        // Target the stack's engine. `--context` must come right after `docker`, before `compose`. Only
        // the `docker compose` form (command == "docker") supports it; the legacy `docker-compose`
        // binary cannot be context-routed, so external stacks require the compose plugin.
        var contextArg = string.Equals(command, "docker", StringComparison.OrdinalIgnoreCase)
            ? _engineContextArg
            : string.Empty;
        var fullArgs = string.IsNullOrEmpty(argPrefix)
            ? $"{contextArg}--project-name {project} {arguments}"
            : $"{contextArg}{argPrefix} --project-name {project} {arguments}";

        var run = await RunProcessAsync(command, fullArgs, workingDirectory, stdin: null, cancellationToken,
            env: new Dictionary<string, string> { ["COMPOSE_PROJECT_NAME"] = project });

        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{command} {fullArgs} failed: {(string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr)}");
        }
    }

    private async Task WaitForDatabaseAsync(
        string stackId, string? stackName, string rootPassword, ApplyPatchResultDto result, CancellationToken cancellationToken)
    {
        var container = DbContainer(stackId, stackName);
        var deadline = DateTime.UtcNow.AddSeconds(120);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ping = await RunProcessAsync(
                "docker",
                $"{_engineContextArg}exec {container} mysqladmin ping -uroot -p{rootPassword} --silent",
                workingDirectory: null, stdin: null, cancellationToken);

            if (ping.ExitCode == 0)
            {
                AddLog(result, "Database is ready.");
                return;
            }

            await Task.Delay(2000, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for the database to become ready.");
    }

    private static string FilterMySqlWarnings(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return stderr;
        }

        var lines = stderr.Split('\n')
            .Where(line => !line.Contains("Using a password on the command line", StringComparison.OrdinalIgnoreCase));
        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Opens a SQL file wrapped so it is executed as a single transaction: a preamble that disables
    /// autocommit and opens a transaction, the file contents, and a trailing COMMIT. Streams the file
    /// rather than buffering it, so large world-DB patches don't have to fit in memory.
    /// </summary>
    private static Stream OpenTransactionalSqlStream(string sqlFile)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var preamble = new MemoryStream(encoding.GetBytes("SET SESSION autocommit=0;\nSTART TRANSACTION;\n"));
        var body = new FileStream(sqlFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Normalize `--`-banner comments (e.g. `--/////`) that WoW SQL exports use: MySQL only treats
        // `--` as a comment when followed by whitespace, so insert a space to keep them from becoming
        // syntax errors. Streamed line-by-line so large world-DB patches don't buffer in memory.
        var normalized = new LineTransformingStream(body, NormalizeSqlCommentLine, encoding);
        var postamble = new MemoryStream(encoding.GetBytes("\nCOMMIT;\n"));
        return new ConcatenatedStream(preamble, normalized, postamble);
    }

    /// <summary>
    /// Ensures a line-leading <c>--</c> comment is followed by whitespace (MySQL rejects <c>--x</c> as
    /// a comment). Only rewrites lines whose first non-blank characters are <c>--</c> immediately
    /// followed by a non-space, so real SQL and operators are never touched.
    /// </summary>
    private static string NormalizeSqlCommentLine(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        if (i + 2 <= line.Length && line[i] == '-' && line[i + 1] == '-')
        {
            var after = i + 2;
            if (after < line.Length && line[after] != ' ' && line[after] != '\t')
            {
                return line[..after] + " " + line[after..];
            }
        }

        return line;
    }

    /// <summary>
    /// Forward-only, read-only stream that reads several source streams back-to-back. Used to bracket
    /// a SQL file with BEGIN/COMMIT without copying its contents into memory.
    /// </summary>
    private sealed class ConcatenatedStream : Stream
    {
        private readonly Queue<Stream> _streams;

        public ConcatenatedStream(params Stream[] streams) => _streams = new Queue<Stream>(streams);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_streams.Count > 0)
            {
                var read = _streams.Peek().Read(buffer, offset, count);
                if (read > 0)
                {
                    return read;
                }
                _streams.Dequeue().Dispose();
            }
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_streams.Count > 0)
            {
                var read = await _streams.Peek().ReadAsync(buffer, cancellationToken);
                if (read > 0)
                {
                    return read;
                }
                await _streams.Dequeue().DisposeAsync();
            }
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_streams.Count > 0)
                {
                    _streams.Dequeue().Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Forward-only, read-only stream that reads a source stream line by line, applies a transform to
    /// each line, and re-emits it with a trailing <c>\n</c>. Used to normalize SQL comments on the fly
    /// without buffering the whole file.
    /// </summary>
    private sealed class LineTransformingStream : Stream
    {
        private readonly StreamReader _reader;
        private readonly Func<string, string> _transform;
        private readonly Encoding _encoding;
        private byte[] _buffer = Array.Empty<byte>();
        private int _bufferPos;
        private bool _eof;

        public LineTransformingStream(Stream source, Func<string, string> transform, Encoding encoding)
        {
            _reader = new StreamReader(source, encoding, detectEncodingFromByteOrderMarks: true);
            _transform = transform;
            _encoding = encoding;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_bufferPos >= _buffer.Length)
            {
                if (_eof)
                {
                    return 0;
                }

                var line = _reader.ReadLine();
                if (line is null)
                {
                    _eof = true;
                    return 0;
                }

                _buffer = _encoding.GetBytes(_transform(line) + "\n");
                _bufferPos = 0;
            }

            var n = Math.Min(count, _buffer.Length - _bufferPos);
            Array.Copy(_buffer, _bufferPos, buffer, offset, n);
            _bufferPos += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static async Task NormalizeToCrlfAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(source, cancellationToken);
        text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        // WDBXEditor's CSV reader requires every line (including the last) to end with CRLF; a file
        // without a trailing newline has its final row's last two chars stripped and corrupted. Ensure
        // exactly one trailing CRLF.
        if (!text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            text += "\r\n";
        }
        await File.WriteAllTextAsync(destination, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // Redacts a MySQL-style password ("-pSecret") from an argument string before it is logged or
    // attached to a trace. Only matches a standalone short "-p" option (not "--project-name").
    private static readonly Regex PasswordArgRegex = new(@"(?<=^|\s)-p[^\s-]\S*", RegexOptions.Compiled);

    private static string RedactSecrets(string arguments) => PasswordArgRegex.Replace(arguments, "-p***");

    private async Task<ProcessResult> RunProcessAsync(
        string fileName, string arguments, string? workingDirectory, Stream? stdin,
        CancellationToken cancellationToken, IDictionary<string, string>? env = null,
        IReadOnlyList<string>? argumentList = null)
    {
        // When an argument list is given, each element is passed verbatim (no shell/quote re-parsing);
        // otherwise fall back to the single Arguments string.
        var displayArgs = argumentList is not null ? string.Join(' ', argumentList) : arguments;
        var redactedArgs = RedactSecrets(displayArgs);

        using var activity = MigrationTelemetry.ActivitySource.StartActivity("process.exec", ActivityKind.Client);
        activity?.SetTag("process.command", fileName);
        activity?.SetTag("process.args", redactedArgs);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (argumentList is not null)
        {
            foreach (var argument in argumentList)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            startInfo.Arguments = arguments;
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        var stopwatch = Stopwatch.StartNew();
        _logger.LogDebug("exec: {Command} {Args}", fileName, redactedArgs);

        if (!process.Start())
        {
            activity?.SetStatus(ActivityStatusCode.Error, "failed to start process");
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await stdin.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        stopwatch.Stop();

        activity?.SetTag("process.exit_code", process.ExitCode);
        activity?.SetTag("process.elapsed_ms", stopwatch.ElapsedMilliseconds);
        activity?.SetStatus(process.ExitCode == 0 ? ActivityStatusCode.Ok : ActivityStatusCode.Error);

        if (process.ExitCode == 0)
        {
            _logger.LogDebug("exec done: {Command} (exit 0, {ElapsedMs} ms)", fileName, stopwatch.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogWarning(
                "exec failed: {Command} {Args} (exit {ExitCode}, {ElapsedMs} ms): {Error}",
                fileName, redactedArgs, process.ExitCode, stopwatch.ElapsedMilliseconds,
                FilterMySqlWarnings(stderr.ToString()).Trim());
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}
