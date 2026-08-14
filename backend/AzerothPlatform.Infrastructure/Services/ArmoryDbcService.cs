using System.Diagnostics;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Extracts a stack's live server DBCs and converts armory-required tables to CSV on the stack's
/// armory-assets volume. DBC binaries are read from the stack engine; WDBXEditor runs on the manager
/// for external stacks (large tables such as Spell can OOM on small VPS instances).
/// </summary>
public sealed class ArmoryDbcService : IArmoryDbcService
{
    private static readonly string[] RequiredDbcTables =
    [
        "Achievement",
        "Achievement_Category",
        "AreaTable",
        "CharTitles",
        "Faction",
        "GlyphProperties",
        "Item",
        "ItemDisplayInfo",
        "ItemSet",
        "SkillLine",
        "Spell",
        "SpellDuration",
        "SpellIcon",
        "SpellItemEnchantment",
        "SpellRadius",
        "Talent",
        "TalentTab",
    ];

    private readonly AzerothCoreDbContext _dbContext;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly IMigrationImageService _imageService;
    private readonly ArmoryAssetsOptions _assetsOptions;
    private readonly MigrationOptions _migrationOptions;
    private readonly DockerOptions _dockerOptions;
    private readonly ILogger<ArmoryDbcService> _logger;

    public ArmoryDbcService(
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine,
        IMigrationImageService imageService,
        IOptions<ArmoryAssetsOptions> assetsOptions,
        IOptions<MigrationOptions> migrationOptions,
        IOptions<DockerOptions> dockerOptions,
        ILogger<ArmoryDbcService> logger)
    {
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
        _imageService = imageService;
        _assetsOptions = assetsOptions.Value;
        _migrationOptions = migrationOptions.Value;
        _dockerOptions = dockerOptions.Value;
        _logger = logger;
    }

    private static string DataVolumeName(string stackId) =>
        $"{DockerComposeOverrideGenerator.GetComposeProjectName(stackId)}_ac-client-data";

    public bool HasServerDbcs(string stackId)
    {
        var dir = Path.Combine(_assetsOptions.DataPathFor(stackId), "dbc");
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.csv").Any();
    }

    public async Task<ArmoryDbcSyncResultDto> SyncFromServerAsync(
        string stackId, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        void Report(string line) => onProgress?.Invoke(line);

        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken)
            ?? throw new InvalidOperationException($"Stack {stackId} not found.");

        var result = new ArmoryDbcSyncResultDto();
        var workVolume = $"acore-armory-dbc-sync-{Guid.NewGuid():N}";
        var assetsVolume = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stackId);
        var clientDataVolume = DataVolumeName(stackId);

        try
        {
            await _remoteEngine.EnsureVolumeExistsAsync(stack, workVolume, cancellationToken);

            _logger.LogInformation(
                "Copying server DBCs for stack {StackId} from volume {Volume} into work volume {WorkVolume}.",
                stackId, clientDataVolume, workVolume);
            Report($"Copying server DBC binaries from stack volume {clientDataVolume}…");
            var sourceDbcCount = await _remoteEngine.CountVolumeFilesAsync(
                stack, clientDataVolume, "dbc", "*.dbc", cancellationToken);
            if (sourceDbcCount == 0)
            {
                throw new InvalidOperationException(
                    "No DBC files were found in the stack's client-data volume. " +
                    "Start the stack and wait for the client-data-init container to finish, then try again.");
            }

            await _remoteEngine.CopyVolumeSubdirAsync(
                stack, clientDataVolume, "dbc", workVolume, "server_dbc", cancellationToken);

            result.ServerDbcCount = await _remoteEngine.CountVolumeFilesAsync(
                stack, workVolume, "server_dbc", "*.dbc", cancellationToken);
            if (result.ServerDbcCount == 0)
            {
                throw new InvalidOperationException(
                    "DBC files could not be read from the stack's client-data volume. " +
                    "Check that the stack's Docker engine is reachable, then try again.");
            }

            Report($"Found {result.ServerDbcCount} DBC file(s) on the stack.");

            Report("Preparing the DBC conversion tool (WDBXEditor)…");
            await _imageService.EnsureWdbxImageAsync(cancellationToken);

            var convertOnManager = stack.DeploymentTarget == DeploymentTarget.External;
            Report(convertOnManager
                ? $"Converting {RequiredDbcTables.Length} table(s) to CSV on the manager (external stack)…"
                : $"Converting {RequiredDbcTables.Length} table(s) to CSV on the stack engine…");
            var index = 0;
            foreach (var table in RequiredDbcTables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                var tableDbcName = $"{table}.dbc";
                var tablePresent = await _remoteEngine.CountVolumeFilesAsync(
                    stack, workVolume, "server_dbc", tableDbcName, cancellationToken);
                if (tablePresent == 0)
                {
                    result.Failed.Add($"{table}: not present in the server's DBC set");
                    Report($"[{index}/{RequiredDbcTables.Length}] Skipped {table} — not present on the server.");
                    continue;
                }

                try
                {
                    await ExportTableOnStackAsync(stack, workVolume, table, cancellationToken);
                    result.Exported.Add($"{table}.csv");
                    Report($"[{index}/{RequiredDbcTables.Length}] Converted {table}.csv");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to convert DBC {Table} to CSV for stack {StackId}.", table, stackId);
                    result.Failed.Add($"{table}: {ex.Message}");
                    Report($"[{index}/{RequiredDbcTables.Length}] Failed to convert {table}: {ex.Message}");
                }
            }

            if (result.Exported.Count > 0)
            {
                Report("Publishing converted DBC CSVs to the stack armory-assets volume…");
                await _remoteEngine.EnsureVolumeExistsAsync(stack, assetsVolume, cancellationToken);
                await _remoteEngine.CopyVolumeSubdirAsync(
                    stack, workVolume, "csv_out", assetsVolume, "dbc", cancellationToken);
                await _remoteEngine.SetVolumeWorldReadableAsync(stack, assetsVolume, cancellationToken);
            }

            _logger.LogInformation(
                "Armory DBC sync for stack {StackId}: {Exported} exported, {Failed} failed.",
                stackId, result.Exported.Count, result.Failed.Count);

            return result;
        }
        finally
        {
            await _remoteEngine.RemoveVolumeAsync(stack, workVolume, cancellationToken);
        }
    }

    private async Task ExportTableOnStackAsync(
        ManagedStackEntity stack, string workVolume, string table, CancellationToken cancellationToken)
    {
        var exportDir = $"export/{table}";
        var dbcName = $"{table}.dbc";
        var csvName = $"{table}.csv";

        await _remoteEngine.RunVolumeShellAsync(
            stack,
            workVolume,
            $"mkdir -p {exportDir} && cp server_dbc/{dbcName} {exportDir}/",
            cancellationToken);

        if (stack.DeploymentTarget == DeploymentTarget.External)
        {
            await ExportTableUsingManagerEngineAsync(stack, workVolume, exportDir, dbcName, csvName, cancellationToken);
            return;
        }

        var toolArgs = $"-export -f {Quote(dbcName)} -b {_migrationOptions.WoWBuild} -o {Quote(csvName)}";
        var run = await _remoteEngine.RunToolInVolumeSubdirAsync(
            stack, workVolume, exportDir, _migrationOptions.WdbxImage, toolArgs, cancellationToken);

        await EnsureExportSucceededAsync(
            stack,
            workVolume,
            exportDir,
            csvName,
            run.ExitCode,
            run.StdOut,
            run.StdErr,
            cancellationToken);

        await _remoteEngine.RunVolumeShellAsync(
            stack,
            workVolume,
            $"mkdir -p csv_out && test -f {exportDir}/{csvName} && mv {exportDir}/{csvName} csv_out/",
            cancellationToken);
    }

    /// <summary>
    /// Runs WDBXEditor on the manager daemon for external stacks. Fetches one DBC from the remote work
    /// volume, exports locally, then seeds the CSV back into the remote work volume.
    /// </summary>
    private async Task ExportTableUsingManagerEngineAsync(
        ManagedStackEntity stack,
        string workVolume,
        string exportDir,
        string dbcName,
        string csvName,
        CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(GetManagerDataMountPath(), "armory-dbc-export", Guid.NewGuid().ToString("N"));
        var localExportDir = Path.Combine(tempRoot, "export");
        Directory.CreateDirectory(localExportDir);

        try
        {
            await _remoteEngine.FetchVolumeSubdirAsync(stack, workVolume, exportDir, localExportDir, cancellationToken);

            var dbcPath = Path.Combine(localExportDir, dbcName);
            if (!File.Exists(dbcPath))
            {
                throw new InvalidOperationException(
                    $"DBC file '{dbcName}' was not found after fetching from the remote work volume (expected at {dbcPath}).");
            }

            var toolArgs = $"-export -f {Quote(dbcName)} -b {_migrationOptions.WoWBuild} -o {Quote(csvName)}";
            var run = await RunLocalDockerToolAsync(localExportDir, _migrationOptions.WdbxImage, toolArgs, cancellationToken);

            var csvPath = Path.Combine(localExportDir, csvName);
            if (run.ExitCode != 0 || !File.Exists(csvPath))
            {
                throw new InvalidOperationException(FormatToolFailure(run.ExitCode, run.StdOut, run.StdErr, csvPath));
            }

            var pushRoot = Path.Combine(tempRoot, "push");
            var csvOutDir = Path.Combine(pushRoot, "csv_out");
            Directory.CreateDirectory(csvOutDir);
            File.Copy(csvPath, Path.Combine(csvOutDir, csvName), overwrite: true);
            await _remoteEngine.SeedVolumeAsync(stack, workVolume, pushRoot, cancellationToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete temporary armory DBC export directory {Path}.", tempRoot);
            }
        }
    }

    private async Task EnsureExportSucceededAsync(
        ManagedStackEntity stack,
        string workVolume,
        string exportDir,
        string csvName,
        int exitCode,
        string stdout,
        string stderr,
        CancellationToken cancellationToken)
    {
        var csvPresent = await _remoteEngine.CountVolumeFilesAsync(
            stack, workVolume, exportDir, csvName, cancellationToken) > 0;

        if (exitCode == 0 && csvPresent)
        {
            return;
        }

        if (csvPresent)
        {
            _logger.LogWarning(
                "WDBXEditor exited {ExitCode} but produced {Csv}; treating export as successful.",
                exitCode,
                csvName);
            return;
        }

        throw new InvalidOperationException(FormatToolFailure(exitCode, stdout, stderr, csvName));
    }

    private static string FormatToolFailure(int exitCode, string stdout, string stderr, string expectedCsvPath)
    {
        var parts = new List<string> { $"WDBXEditor exited with code {exitCode}." };
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            parts.Add(stderr.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            parts.Add(stdout.Trim());
        }

        parts.Add($"Expected output: {expectedCsvPath}");
        return string.Join(Environment.NewLine, parts);
    }

    private string GetManagerDataMountPath()
    {
        var buildsPath = _dockerOptions.BuildsPath;
        if (string.IsNullOrWhiteSpace(buildsPath))
        {
            return Path.GetTempPath();
        }

        var dataMount = Path.GetDirectoryName(Path.GetFullPath(buildsPath).TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(dataMount) ? Path.GetTempPath() : dataMount;
    }

    /// <summary>
    /// When the manager runs in Docker, tool containers must mount the manager data volume — not a path
    /// under <c>/tmp</c> inside the manager container, which the host daemon cannot see.
    /// </summary>
    private (string MountArgs, string WorkDir) ResolveLocalToolMount(string hostWorkDir)
    {
        if (TryGetDataVolumeSubpath(hostWorkDir, out var relative)
            && !string.IsNullOrWhiteSpace(_dockerOptions.DataVolumeName))
        {
            var containerWork = string.IsNullOrEmpty(relative) ? "/data" : $"/data/{relative}";
            return ($"-v {_dockerOptions.DataVolumeName}:/data", containerWork);
        }

        return ($"-v \"{hostWorkDir}\":/w", "/w");
    }

    private bool TryGetDataVolumeSubpath(string localSourceDir, out string relative)
    {
        relative = string.Empty;
        var dataMount = GetManagerDataMountPath();
        if (string.Equals(Path.GetFullPath(dataMount), Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fullSource = Path.GetFullPath(localSourceDir);
        var normalizedMount = dataMount.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(fullSource, normalizedMount, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = normalizedMount + Path.DirectorySeparatorChar;
        if (!fullSource.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        relative = fullSource[prefix.Length..].Replace('\\', '/').Trim('/');
        return true;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunLocalDockerToolAsync(
        string hostWorkDir,
        string image,
        string toolArgs,
        CancellationToken cancellationToken)
    {
        var (mountArgs, workDir) = ResolveLocalToolMount(hostWorkDir);
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"run --rm --memory 4g --memory-swap 4g {mountArgs} -w {workDir} {image} {toolArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start docker process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
