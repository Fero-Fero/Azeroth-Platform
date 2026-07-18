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
/// Extracts a stack's live server DBCs and converts the armory-required tables to CSV files entirely on
/// the stack's Docker engine (work volume + armory-assets volume). The manager orchestrates containers
/// but does not persist DBC/CSV artifacts under its data directory.
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
    private readonly ILogger<ArmoryDbcService> _logger;

    public ArmoryDbcService(
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine,
        IMigrationImageService imageService,
        IOptions<ArmoryAssetsOptions> assetsOptions,
        IOptions<MigrationOptions> migrationOptions,
        ILogger<ArmoryDbcService> logger)
    {
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
        _imageService = imageService;
        _assetsOptions = assetsOptions.Value;
        _migrationOptions = migrationOptions.Value;
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

            Report($"Converting {RequiredDbcTables.Length} table(s) to CSV on the stack engine…");
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

        var toolArgs = $"-export -f \"{dbcName}\" -b {_migrationOptions.WoWBuild} -o \"{csvName}\"";
        var run = await _remoteEngine.RunToolInVolumeSubdirAsync(
            stack, workVolume, exportDir, _migrationOptions.WdbxImage, toolArgs, cancellationToken);

        if (run.ExitCode != 0)
        {
            var output = string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(output) ? $"WDBXEditor exited with code {run.ExitCode}." : output.Trim());
        }

        await _remoteEngine.RunVolumeShellAsync(
            stack,
            workVolume,
            $"mkdir -p csv_out && test -f {exportDir}/{csvName} && mv {exportDir}/{csvName} csv_out/",
            cancellationToken);
    }
}
