using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services.Migrations;
using AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

public sealed class ModuleInstallOrchestrator : IModuleInstallOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] SqlDatabaseOrder = ["acore_world", "acore_auth", "acore_characters"];

    private readonly IDbcBaselineStore _dbcStore;
    private readonly IModuleInstallHookRunner _hooks;
    private readonly IWdbxCli _wdbx;
    private readonly IMpqToolCli _mpqTool;
    private readonly IModulePackageStorage _packages;
    private readonly IMigrationService _migrations;
    private readonly IAddonService _addons;
    private readonly IServerConfigService _serverConfig;
    private readonly AzerothCoreDbContext _db;
    private readonly DockerOptions _docker;
    private readonly MigrationOptions _migrationOptions;
    private readonly ILogger<ModuleInstallOrchestrator> _logger;

    public ModuleInstallOrchestrator(
        IDbcBaselineStore dbcStore,
        IModuleInstallHookRunner hooks,
        IWdbxCli wdbx,
        IMpqToolCli mpqTool,
        IModulePackageStorage packages,
        IMigrationService migrations,
        IAddonService addons,
        IServerConfigService serverConfig,
        AzerothCoreDbContext db,
        IOptions<DockerOptions> docker,
        IOptions<MigrationOptions> migrationOptions,
        ILogger<ModuleInstallOrchestrator> logger)
    {
        _dbcStore = dbcStore;
        _hooks = hooks;
        _wdbx = wdbx;
        _mpqTool = mpqTool;
        _packages = packages;
        _migrations = migrations;
        _addons = addons;
        _serverConfig = serverConfig;
        _db = db;
        _docker = docker.Value;
        _migrationOptions = migrationOptions.Value;
        _logger = logger;
    }

    public async Task<StackModuleInstallChoicesDto> DescribeChoicesAsync(
        string stackId, CancellationToken cancellationToken = default)
    {
        var moduleIds = await LoadModuleIdsAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var modules = new List<ModuleInstallChoicesDto>();
        using var session = new ModuleInstallSession(InstalledModulesLayout.Root(stackRoot));

        foreach (var moduleId in moduleIds)
        {
            var hook = _hooks.Find(moduleId);
            if (hook is null)
            {
                continue;
            }

            var packageRoot = ResolvePackageRoot(stackRoot, moduleId, required: false);
            var helpers = new ModuleInstallHelpers(moduleId, packageRoot, session, _wdbx, _dbcStore, _mpqTool);
            var context = new ModuleInstallContext
            {
                ModuleId = moduleId,
                PackageRoot = packageRoot,
                StackId = Guid.TryParse(stackId, out var guid) ? guid : null,
                Session = session,
                Helpers = helpers,
                Selections = new ModuleInstallSelections()
            };
            var groups = await hook.DescribeChoicesAsync(context, cancellationToken);
            if (groups.Count == 0)
            {
                continue;
            }

            modules.Add(new ModuleInstallChoicesDto { ModuleId = moduleId, Groups = groups });
        }

        return new StackModuleInstallChoicesDto
        {
            Modules = modules,
            Saved = InstalledModulesLayout.LoadChoices(stackRoot),
            Status = InstalledModulesLayout.LoadStatus(stackRoot)
        };
    }

    public void SaveChoices(string stackId, ApplyModuleExtraDataRequest request)
    {
        var stackRoot = GetStackRoot(stackId);
        InstalledModulesLayout.SaveChoices(stackRoot, request);
    }

    public Task SaveChoicesAsync(
        string stackId, ApplyModuleExtraDataRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveChoices(stackId, request);
        return Task.CompletedTask;
    }

    public ModuleExtraDataStackStatusDto GetStackStatus(string stackId) =>
        InstalledModulesLayout.LoadStatus(GetStackRoot(stackId));

    public async Task ApplyAsync(
        string stackId,
        ApplyModuleExtraDataRequest request,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default)
    {
        await PrepareAsync(stackId, request, onProgress, cancellationToken);
        var canDeposit = await _migrations.TryEnsureServerDbcBaselineAsync(stackId, cancellationToken);
        if (!canDeposit)
        {
            onProgress?.Invoke(
                "Module extras were prepared under InstalledModules. Setup module content waits until the stack has populated /data/dbc.");
            return;
        }

        await DepositAsync(stackId, onProgress, cancellationToken);
    }

    public async Task PrepareAsync(
        string stackId,
        ApplyModuleExtraDataRequest request,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default)
    {
        if (!_dbcStore.IsReady())
        {
            throw new InvalidOperationException(
                "Sync the DBC baseline first (Settings / Patches → Sync DBC baseline). Extra-data prepare cannot trim without it.");
        }

        var moduleIds = await LoadModuleIdsAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        if (request.IpContentMode == IpContentMode.Unset && request.SelectionsByModuleId.Count == 0)
        {
            request = InstalledModulesLayout.LoadChoices(stackRoot);
        }

        InstalledModulesLayout.SaveChoices(stackRoot, request);
        using var session = new ModuleInstallSession(InstalledModulesLayout.Root(stackRoot));

        try
        {
            var skipIp = request.IpContentMode == IpContentMode.ServerWideProgression;
            var helpersByModule = new Dictionary<string, ModuleInstallHelpers>(StringComparer.OrdinalIgnoreCase);
            foreach (var moduleId in moduleIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hook = _hooks.Find(moduleId);
                if (hook is null)
                {
                    continue;
                }

                if (skipIp && string.Equals(moduleId, IndividualProgressionInstallHook.CatalogId, StringComparison.OrdinalIgnoreCase))
                {
                    onProgress?.Invoke($"Skipping {moduleId} extras (Server Wide Progression mode).");
                    var leftover = InstalledModulesLayout.ModuleDir(stackRoot, moduleId);
                    if (Directory.Exists(leftover))
                    {
                        Directory.Delete(leftover, recursive: true);
                    }

                    continue;
                }

                onProgress?.Invoke($"Preparing extra data for {moduleId}…");
                InstalledModulesLayout.TruncateModule(stackRoot, moduleId);
                var packageRoot = ResolvePackageRoot(stackRoot, moduleId, required: true);
                var helpers = new ModuleInstallHelpers(moduleId, packageRoot, session, _wdbx, _dbcStore, _mpqTool);
                helpersByModule[moduleId] = helpers;
                request.SelectionsByModuleId.TryGetValue(moduleId, out var selections);
                var context = new ModuleInstallContext
                {
                    ModuleId = moduleId,
                    PackageRoot = packageRoot,
                    StackId = Guid.TryParse(stackId, out var guid) ? guid : null,
                    Session = session,
                    Helpers = helpers,
                    Selections = selections ?? new ModuleInstallSelections()
                };
                await hook.InstallAsync(context, cancellationToken);
            }

            onProgress?.Invoke("Trimming DBC deltas against the baseline…");
            foreach (var helpers in helpersByModule.Values)
            {
                await helpers.TrimAllDbcs(cancellationToken);
            }

            var csvSources = session.BaseDbc is { } sessionBase
                ? InstalledModulesLayout.CollectCsvSources(stackRoot, moduleIds, sessionBase)
                : InstalledModulesLayout.CollectCsvSources(stackRoot, moduleIds);
            onProgress?.Invoke("Coalescing DBC contributions…");
            await DbcCoalesceHelper.CoalesceAsync(csvSources, cancellationToken);

            foreach (var (moduleId, helpers) in helpersByModule)
            {
                foreach (var mpq in helpers.Contribution.Artifacts.Where(a => a.Kind == ModuleInstallArtifactKind.Mpq).ToList())
                {
                    var stripped = await StripDbcsOrOriginalAsync(mpq.SourcePath, stackRoot, cancellationToken);
                    if (stripped is null)
                    {
                        File.Delete(mpq.SourcePath);
                        continue;
                    }

                    if (!string.Equals(stripped, mpq.SourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(stripped, mpq.SourcePath, overwrite: true);
                    }
                }

                WriteManifest(stackRoot, moduleId, helpers, request);
            }

            var status = InstalledModulesLayout.LoadStatus(stackRoot);
            status.Prepared = helpersByModule.Count > 0;
            status.Deposited = false;
            status.IpContentMode = request.IpContentMode;
            InstalledModulesLayout.SaveStatus(stackRoot, status);
        }
        catch
        {
            session.MarkFailed();
            throw;
        }
    }

    public async Task DepositAsync(
        string stackId,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default)
    {
        var moduleIds = await LoadModuleIdsAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var canDeposit = await _migrations.TryEnsureServerDbcBaselineAsync(stackId, cancellationToken);
        if (!canDeposit)
        {
            throw new InvalidOperationException(
                "Start the stack once so client-data-init populates /data/dbc before Setup module content.");
        }

        await DepositFromInstalledModulesAsync(stackId, stackRoot, moduleIds, onProgress, cancellationToken);
        var status = InstalledModulesLayout.LoadStatus(stackRoot);
        status.Deposited = true;
        InstalledModulesLayout.SaveStatus(stackRoot, status);
    }

    public async Task RemoveModuleExtrasAsync(
        string stackId,
        string moduleId,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default)
    {
        var stackRoot = GetStackRoot(stackId);
        var mpqDir = InstalledModulesLayout.MpqDir(stackRoot, moduleId);
        if (Directory.Exists(mpqDir))
        {
            foreach (var mpq in Directory.EnumerateFiles(mpqDir, "*.*"))
            {
                var overlay = Path.Combine(
                    MigrationLayout.ClientOverlayDataDir(stackRoot), Path.GetFileName(mpq));
                if (File.Exists(overlay))
                {
                    File.Delete(overlay);
                }
            }
        }

        var dir = InstalledModulesLayout.ModuleDir(stackRoot, moduleId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        onProgress?.Invoke($"Removed InstalledModules/{moduleId}.");
        var remaining = InstalledModulesLayout.EnumerateModuleDirs(stackRoot).Any();
        if (remaining)
        {
            await DepositAsync(stackId, onProgress, cancellationToken);
        }
    }

    private async Task DepositFromInstalledModulesAsync(
        string stackId,
        string stackRoot,
        IReadOnlyList<string> moduleIds,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var serverDbcDir = MigrationLayout.ServerDbcDir(stackRoot);
        Directory.CreateDirectory(serverDbcDir);
        var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var baseDbc = InstalledModulesLayout.FindBaseDbc(stackRoot, moduleIds);
        if (baseDbc is { } declared && File.Exists(declared.BinaryPath))
        {
            var dest = Path.Combine(serverDbcDir, $"{declared.TableName}.dbc");
            File.Copy(declared.BinaryPath, dest, overwrite: true);
            updated.Add(Path.GetFileName(dest));
            onProgress?.Invoke($"Using {declared.ModuleId}'s {declared.TableName}.dbc as the import base.");
        }

        var coalesced = await DbcCoalesceHelper.CoalesceAsync(
            InstalledModulesLayout.CollectCsvSources(stackRoot, moduleIds),
            cancellationToken);

        var workDir = Path.Combine(stackRoot, ".migration-tmp", $"module-extras-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            foreach (var table in coalesced)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dbcName = $"{table.TableName}.dbc";
                var workDbc = Path.Combine(workDir, dbcName);
                var start = Path.Combine(serverDbcDir, dbcName);
                if (!File.Exists(start))
                {
                    throw new InvalidOperationException(
                        $"No live server DBC for {dbcName}. Start the stack once so client-data-init populates /data/dbc.");
                }

                File.Copy(start, workDbc, overwrite: true);
                var csvPath = Path.Combine(workDir, CsvNormalizer.TableFileName(table.TableName));
                await CsvNormalizer.WriteCrlfAsync(csvPath, table.CsvText, cancellationToken);
                onProgress?.Invoke($"Importing {table.TableName}.txt into {dbcName}…");
                await _wdbx.ImportCsvAsync(workDbc, csvPath, cancellationToken);
                File.Copy(workDbc, start, overwrite: true);
                updated.Add(dbcName);
            }
        }
        finally
        {
            TryDelete(workDir);
        }

        if (updated.Count > 0)
        {
            onProgress?.Invoke("Pushing updated DBC files to the live server…");
            await _migrations.PushServerDbcFilesAsync(stackId, updated.ToList(), cancellationToken);
            onProgress?.Invoke("Rebuilding patch-D.MPQ…");
            await _migrations.RebuildPatchDAsync(stackId, cancellationToken);
        }

        var hints = new List<WorldserverConfHint>();
        var sqlByDb = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["acore_world"] = [],
            ["acore_auth"] = [],
            ["acore_characters"] = []
        };

        foreach (var moduleId in moduleIds)
        {
            var manifest = InstalledModulesLayout.LoadManifest(stackRoot, moduleId);
            if (manifest is null)
            {
                continue;
            }

            hints.AddRange(manifest.ConfHints);

            var mpqDir = InstalledModulesLayout.MpqDir(stackRoot, moduleId);
            if (Directory.Exists(mpqDir))
            {
                foreach (var mpq in Directory.EnumerateFiles(mpqDir, "*.*"))
                {
                    onProgress?.Invoke($"Publishing overlay MPQ {Path.GetFileName(mpq)}…");
                    await _migrations.PublishOverlayMpqAsync(stackId, mpq, cancellationToken);
                }
            }

            var otherDir = InstalledModulesLayout.OtherDir(stackRoot, moduleId);
            if (Directory.Exists(otherDir))
            {
                foreach (var addonDir in Directory.EnumerateDirectories(otherDir))
                {
                    var folder = Path.GetFileName(addonDir);
                    onProgress?.Invoke($"Installing addon {folder} from {moduleId}…");
                    await _addons.InstallFromDirectoryAsync(stackId, addonDir, folder, cancellationToken);
                }
            }

            var luaDir = InstalledModulesLayout.LuaDir(stackRoot, moduleId);
            if (Directory.Exists(luaDir))
            {
                var destRoot = MigrationLayout.LuaScriptsDir(stackRoot);
                Directory.CreateDirectory(destRoot);
                foreach (var file in Directory.EnumerateFiles(luaDir, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(luaDir, file).Replace('\\', '/');
                    onProgress?.Invoke($"Installing Lua {relative} from {moduleId}…");
                    var dest = Path.Combine(destRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                }
            }

            var mapsRoot = InstalledModulesLayout.MapsDir(stackRoot, moduleId);
            if (Directory.Exists(mapsRoot))
            {
                foreach (var sub in InstalledModulesLayout.DataVolumeSubdirs)
                {
                    var dir = Path.Combine(mapsRoot, sub);
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }

                    var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
                    if (files.Count == 0)
                    {
                        continue;
                    }

                    onProgress?.Invoke($"Publishing {files.Count} {sub} file(s) from {moduleId}…");
                    await _migrations.PublishDataVolumeFilesAsync(stackId, sub, files, cancellationToken);
                }
            }

            var sqlRoot = InstalledModulesLayout.SqlDir(stackRoot, moduleId);
            if (Directory.Exists(sqlRoot))
            {
                foreach (var sql in Directory.EnumerateFiles(sqlRoot, "*.sql", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(sqlRoot, sql).Replace('\\', '/');
                    var db = relative.Contains("/auth/", StringComparison.OrdinalIgnoreCase)
                             || relative.StartsWith("auth/", StringComparison.OrdinalIgnoreCase)
                        ? "acore_auth"
                        : relative.Contains("/character", StringComparison.OrdinalIgnoreCase)
                             || relative.StartsWith("character", StringComparison.OrdinalIgnoreCase)
                            ? "acore_characters"
                            : "acore_world";
                    sqlByDb[db].Add(sql);
                }
            }
        }

        foreach (var database in SqlDatabaseOrder)
        {
            if (sqlByDb[database].Count == 0)
            {
                continue;
            }

            onProgress?.Invoke($"Applying {sqlByDb[database].Count} SQL file(s) to {database}…");
            try
            {
                await _migrations.ApplySqlFilesAsync(stackId, database, sqlByDb[database], cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Module extra-data SQL aborted on {database}. Earlier databases in this run may already have committed. {ex.Message}",
                    ex);
            }
        }

        if (hints.Count > 0)
        {
            onProgress?.Invoke("Applying worldserver.conf hints…");
            try
            {
                var conf = await _serverConfig.ReadAsync(stackId, "worldserver.conf", cancellationToken);
                var updatedConf = MigrationService.ApplyConfHints(conf.Content, hints);
                await _serverConfig.SaveAsync(stackId, "worldserver.conf", updatedConf, cancellationToken);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogWarning(ex, "worldserver.conf not available yet; conf hints were skipped.");
                onProgress?.Invoke("worldserver.conf is not generated yet; conf hints were skipped.");
            }
        }
    }

    private async Task<string?> StripDbcsOrOriginalAsync(
        string mpqPath, string stackRoot, CancellationToken cancellationToken)
    {
        var probeDir = Path.Combine(stackRoot, ".migration-tmp", $"mpq-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeDir);
        try
        {
            try
            {
                await _wdbx.ExtractDbcsFromMpqAsync(mpqPath, probeDir, filterName: null, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // No DBC/DB2 in the archive (typical for visual patch-J / patch-U).
                return mpqPath;
            }

            var hasDbc = Directory.EnumerateFiles(probeDir, "*.*", SearchOption.AllDirectories)
                .Any(path =>
                    path.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".db2", StringComparison.OrdinalIgnoreCase));
            if (!hasDbc)
            {
                return mpqPath;
            }

            var extractDir = Path.Combine(stackRoot, ".migration-tmp", $"mpq-strip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extractDir);
            try
            {
                await _mpqTool.ExtractAllAsync(mpqPath, extractDir, cancellationToken);
                foreach (var file in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories).ToList())
                {
                    if (file.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".db2", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                    }
                }

                foreach (var dir in Directory.EnumerateDirectories(extractDir, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length)
                             .ToList())
                {
                    if (string.Equals(Path.GetFileName(dir), "DBFilesClient", StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.Delete(dir, recursive: true);
                        continue;
                    }

                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, recursive: false);
                    }
                }

                var remaining = Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories).ToList();
                if (remaining.Count == 0)
                {
                    return null;
                }

                var packOut = Path.Combine(
                    Path.GetDirectoryName(extractDir) ?? extractDir,
                    "stripped-" + Path.GetFileName(mpqPath));
                await _mpqTool.PackPreservePathsAsync(extractDir, packOut, cancellationToken);
                var packed = Path.Combine(
                    Path.GetDirectoryName(mpqPath) ?? extractDir,
                    Path.GetFileName(mpqPath));
                File.Copy(packOut, packed, overwrite: true);
                return packed;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to strip DBC files from {Path.GetFileName(mpqPath)}. The original archive was not copied (a later letter would hide patch-D). {ex.Message}",
                    ex);
            }
            finally
            {
                TryDelete(extractDir);
            }
        }
        finally
        {
            TryDelete(probeDir);
        }
    }

    private static void WriteManifest(
        string stackRoot,
        string moduleId,
        ModuleInstallHelpers helpers,
        ApplyModuleExtraDataRequest request)
    {
        request.SelectionsByModuleId.TryGetValue(moduleId, out var selections);
        File.WriteAllText(
            Path.Combine(InstalledModulesLayout.ModuleDir(stackRoot, moduleId), InstalledModulesLayout.SelectionsFileName),
            JsonSerializer.Serialize(selections ?? new ModuleInstallSelections(), JsonOptions));

        var csvDir = InstalledModulesLayout.CsvDir(stackRoot, moduleId);
        var tables = Directory.Exists(csvDir)
            ? Directory.EnumerateFiles(csvDir, "*.txt")
                .Select(path => CsvNormalizer.NormalizeTableName(Path.GetFileName(path)))
                .ToList()
            : [];

        var entryIds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(csvDir))
        {
            foreach (var csv in Directory.EnumerateFiles(csvDir, "*.txt"))
            {
                var table = CsvNormalizer.NormalizeTableName(Path.GetFileName(csv));
                var ids = File.ReadAllLines(csv)
                    .Skip(1)
                    .Select(CsvNormalizer.FirstCsvField)
                    .Where(id => id.Length > 0)
                    .ToList();
                entryIds[table] = ids;
            }
        }

        var mpqDir = InstalledModulesLayout.MpqDir(stackRoot, moduleId);
        var sqlDir = InstalledModulesLayout.SqlDir(stackRoot, moduleId);
        var otherDir = InstalledModulesLayout.OtherDir(stackRoot, moduleId);
        var luaDir = InstalledModulesLayout.LuaDir(stackRoot, moduleId);
        var mapsDir = InstalledModulesLayout.MapsDir(stackRoot, moduleId);
        var baseArtifact = helpers.Contribution.Artifacts.FirstOrDefault(a => a.Kind == ModuleInstallArtifactKind.DbcBase);

        InstalledModulesLayout.SaveManifest(stackRoot, moduleId, new InstalledModuleManifest
        {
            ModuleId = moduleId,
            Tables = tables,
            EntryIds = entryIds,
            Mpq = Directory.Exists(mpqDir)
                ? Directory.EnumerateFiles(mpqDir).Select(Path.GetFileName).OfType<string>().ToList()
                : [],
            Sql = Directory.Exists(sqlDir)
                ? Directory.EnumerateFiles(sqlDir, "*.sql", SearchOption.AllDirectories)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .ToList()
                : [],
            Addons = Directory.Exists(otherDir)
                ? Directory.EnumerateDirectories(otherDir).Select(Path.GetFileName).OfType<string>().ToList()
                : [],
            Lua = Directory.Exists(luaDir)
                ? Directory.EnumerateFiles(luaDir, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(luaDir, path).Replace('\\', '/'))
                    .ToList()
                : [],
            Maps = Directory.Exists(mapsDir)
                ? InstalledModulesLayout.DataVolumeSubdirs
                    .Where(sub => Directory.Exists(Path.Combine(mapsDir, sub)))
                    .ToList()
                : [],
            BaseDbc = baseArtifact is null
                ? null
                : new InstalledModuleBaseDbc
                {
                    TableName = baseArtifact.DestHint ?? "Spell",
                    ModuleId = moduleId
                },
            ConfHints = helpers.Contribution.ConfHints.ToList()
        });
    }

    private async Task<List<string>> LoadModuleIdsAsync(string stackId, CancellationToken cancellationToken)
    {
        var stack = await _db.ManagedStacks.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");
        return JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson, JsonOptions) ?? [];
    }

    private string ResolvePackageRoot(string stackRoot, string moduleId, bool required)
    {
        var stackModule = Path.Combine(stackRoot, "azerothcore-wotlk", "modules", moduleId);
        if (Directory.Exists(stackModule))
        {
            return stackModule;
        }

        if (_packages.HasPackage(moduleId))
        {
            return _packages.GetPackageDirectory(moduleId);
        }

        if (required)
        {
            throw new InvalidOperationException(
                $"Module '{moduleId}' is not on disk yet. Rebuild the stack or upload the package first.");
        }

        return stackModule;
    }

    private string GetStackRoot(string stackId)
    {
        var builds = Path.IsPathRooted(_docker.BuildsPath)
            ? _docker.BuildsPath
            : Path.GetFullPath(_docker.BuildsPath);
        return Path.Combine(builds, stackId);
    }

    private static void TryDelete(string path)
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
            // best-effort
        }
    }
}
