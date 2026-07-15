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
/// Extracts a stack's live server DBCs and converts the armory-required tables to the CSV files the
/// armory reads from <c>data/dbc</c>, writing them into the stack's uploaded armory dataset so the next
/// armory image build bakes them in. Uses the same WDBXEditor tool image and work-volume execution as
/// the patch pipeline. Only the 3.3.5 tables the armory consumes are converted; the retail transmog
/// tables (<c>dbc_transmog/</c>) have no server equivalent and are left to the uploaded data bundle.
/// </summary>
public sealed class ArmoryDbcService : IArmoryDbcService
{
    // The armory's DbcReader loads these CSVs from data/dbc (see frontend-armory .../data/DbcReader.ts).
    // Each is produced by exporting the same-named server DBC to CSV. Names match the armory's expected
    // file names exactly (case-sensitive on the armory's Linux filesystem).
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

    private string DbcDatasetDir(string stackId) => Path.Combine(_assetsOptions.DataPathFor(stackId), "dbc");

    private static string DataVolumeName(string stackId) =>
        $"{DockerComposeOverrideGenerator.GetComposeProjectName(stackId)}_ac-client-data";

    public bool HasServerDbcs(string stackId)
    {
        var dir = DbcDatasetDir(stackId);
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.csv").Any();
    }

    public async Task<ArmoryDbcSyncResultDto> SyncFromServerAsync(
        string stackId, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        void Report(string line) => onProgress?.Invoke(line);

        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken)
            ?? throw new InvalidOperationException($"Stack {stackId} not found.");

        var result = new ArmoryDbcSyncResultDto();

        // 1) Pull the live binary DBCs (/data/dbc) from the stack's client-data volume to the manager.
        var stagingRoot = Path.Combine(Path.GetTempPath(), "armory-dbc", $"{stackId}-{Guid.NewGuid():N}");
        var serverDbcDir = Path.Combine(stagingRoot, "server_dbc");
        Directory.CreateDirectory(serverDbcDir);

        try
        {
            _logger.LogInformation("Extracting server DBCs for stack {StackId} from volume {Volume}.", stackId, DataVolumeName(stackId));
            Report($"Fetching server DBC files from volume {DataVolumeName(stackId)}…");
            try
            {
                await _remoteEngine.FetchVolumeSubdirAsync(stack, DataVolumeName(stackId), "dbc", serverDbcDir, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not read the server's DBC files. Start the stack at least once so its client data is populated, then try again. " +
                    ex.Message, ex);
            }

            var serverDbcs = Directory.Exists(serverDbcDir)
                ? Directory.EnumerateFiles(serverDbcDir, "*.dbc").ToList()
                : [];
            result.ServerDbcCount = serverDbcs.Count;
            if (serverDbcs.Count == 0)
            {
                throw new InvalidOperationException(
                    "No DBC files were found on the server. Start the stack at least once so its client data is populated, then try again.");
            }

            Report($"Fetched {serverDbcs.Count} DBC file(s) from the server.");

            // 2) Ensure the WDBXEditor tool image exists (built once, then cached).
            Report("Preparing the DBC conversion tool (WDBXEditor)…");
            await _imageService.EnsureWdbxImageAsync(cancellationToken);

            var datasetDbcDir = DbcDatasetDir(stackId);
            Directory.CreateDirectory(datasetDbcDir);

            // 3) Convert each required table to CSV. Runs WDBXEditor once per table with a minimal work
            //    dir (just that one .dbc), then copies the produced CSV into the armory dataset.
            Report($"Converting {RequiredDbcTables.Length} table(s) to CSV…");
            var index = 0;
            foreach (var table in RequiredDbcTables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                var sourceDbc = serverDbcs.FirstOrDefault(
                    p => string.Equals(Path.GetFileNameWithoutExtension(p), table, StringComparison.OrdinalIgnoreCase));
                if (sourceDbc is null)
                {
                    result.Failed.Add($"{table}: not present in the server's DBC set");
                    Report($"[{index}/{RequiredDbcTables.Length}] Skipped {table} — not present on the server.");
                    continue;
                }

                try
                {
                    await ExportTableAsync(stack, stagingRoot, sourceDbc, table, datasetDbcDir, cancellationToken);
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

            _logger.LogInformation(
                "Armory DBC sync for stack {StackId}: {Exported} exported, {Failed} failed.",
                stackId, result.Exported.Count, result.Failed.Count);

            return result;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task ExportTableAsync(
        ManagedStackEntity stack, string stagingRoot, string sourceDbc, string table, string datasetDbcDir,
        CancellationToken cancellationToken)
    {
        var dbcName = $"{table}.dbc";
        var csvName = $"{table}.csv";

        // Minimal per-table work dir so each tool run seeds/fetches only a single DBC.
        var workDir = Path.Combine(stagingRoot, "work", table);
        Directory.CreateDirectory(workDir);
        try
        {
            File.Copy(sourceDbc, Path.Combine(workDir, dbcName), overwrite: true);

            // WDBXEditor loads the DBC from the work dir (WORKDIR=/work) and writes the CSV alongside it.
            var toolArgs = $"-export -f \"{dbcName}\" -b {_migrationOptions.WoWBuild} -o \"{csvName}\"";
            var run = await _remoteEngine.RunToolWithWorkVolumeAsync(
                stack, workDir, _migrationOptions.WdbxImage, toolArgs, cancellationToken);

            var producedCsv = Path.Combine(workDir, csvName);
            if (run.ExitCode != 0 || !File.Exists(producedCsv))
            {
                var output = string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr;
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(output) ? $"WDBXEditor exited with code {run.ExitCode}." : output.Trim());
            }

            File.Copy(producedCsv, Path.Combine(datasetDbcDir, csvName), overwrite: true);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of temp artifacts.
        }
    }
}
