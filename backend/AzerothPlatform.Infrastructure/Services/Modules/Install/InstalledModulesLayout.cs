using System.Text.Json;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

/// <summary>Permanent per-stack extra-data ledger: {stack}/InstalledModules/{moduleId}/…</summary>
public static class InstalledModulesLayout
{
    public const string DirName = "InstalledModules";
    public const string ChoicesFileName = "choices.json";
    public const string StatusFileName = "status.json";
    public const string ManifestFileName = "manifest.json";
    public const string SelectionsFileName = "selections.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Root(string stackRoot) => Path.Combine(stackRoot, DirName);

    public static string ModuleDir(string stackRoot, string moduleId) =>
        Path.Combine(Root(stackRoot), Sanitize(moduleId));

    public static string DbcDir(string stackRoot, string moduleId) =>
        Path.Combine(ModuleDir(stackRoot, moduleId), "dbc");

    public static string CsvDir(string stackRoot, string moduleId) =>
        Path.Combine(ModuleDir(stackRoot, moduleId), "csv");

    public static string MpqDir(string stackRoot, string moduleId) =>
        Path.Combine(ModuleDir(stackRoot, moduleId), "mpq");

    public static string SqlDir(string stackRoot, string moduleId) =>
        Path.Combine(ModuleDir(stackRoot, moduleId), "sql");

    public static string OtherDir(string stackRoot, string moduleId) =>
        Path.Combine(ModuleDir(stackRoot, moduleId), "other");

    public static string LuaDir(string stackRoot, string moduleId) =>
        Path.Combine(ModuleDir(stackRoot, moduleId), "lua");

    public static string MapsDir(string stackRoot, string moduleId) =>
        Path.Combine(ModuleDir(stackRoot, moduleId), "maps");

    public static readonly string[] DataVolumeSubdirs = ["maps", "mmaps", "vmaps"];

    public static string ChoicesPath(string stackRoot) => Path.Combine(Root(stackRoot), ChoicesFileName);

    public static string StatusPath(string stackRoot) => Path.Combine(Root(stackRoot), StatusFileName);

    public static string ManifestPath(string stackRoot, string moduleId) =>
        Path.Combine(ModuleDir(stackRoot, moduleId), ManifestFileName);

    public static string Sanitize(string moduleId)
    {
        if (moduleId.Contains("..", StringComparison.Ordinal) || moduleId.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException($"Invalid module id: {moduleId}");
        }

        return moduleId;
    }

    public static ApplyModuleExtraDataRequest LoadChoices(string stackRoot)
    {
        var path = ChoicesPath(stackRoot);
        if (!File.Exists(path))
        {
            return new ApplyModuleExtraDataRequest();
        }

        return JsonSerializer.Deserialize<ApplyModuleExtraDataRequest>(File.ReadAllText(path), JsonOptions)
               ?? new ApplyModuleExtraDataRequest();
    }

    public static void SaveChoices(string stackRoot, ApplyModuleExtraDataRequest request)
    {
        Directory.CreateDirectory(Root(stackRoot));
        File.WriteAllText(ChoicesPath(stackRoot), JsonSerializer.Serialize(request, JsonOptions));
    }

    public static ModuleExtraDataStackStatusDto LoadStatus(string stackRoot)
    {
        var path = StatusPath(stackRoot);
        var status = File.Exists(path)
            ? JsonSerializer.Deserialize<ModuleExtraDataStackStatusDto>(File.ReadAllText(path), JsonOptions)
              ?? new ModuleExtraDataStackStatusDto()
            : new ModuleExtraDataStackStatusDto();

        var choices = LoadChoices(stackRoot);
        status.IpContentMode = choices.IpContentMode;
        status.HasExtras = EnumerateModuleDirs(stackRoot).Any();
        status.Prepared = status.HasExtras
            && EnumerateModuleDirs(stackRoot).Any(dir => File.Exists(Path.Combine(dir, ManifestFileName)));
        status.HasPendingDeposit = status.Prepared && !status.Deposited;
        return status;
    }

    public static void SaveStatus(string stackRoot, ModuleExtraDataStackStatusDto status)
    {
        Directory.CreateDirectory(Root(stackRoot));
        File.WriteAllText(StatusPath(stackRoot), JsonSerializer.Serialize(status, JsonOptions));
    }

    public static void TruncateModule(string stackRoot, string moduleId)
    {
        var dir = ModuleDir(stackRoot, moduleId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
    }

    public static IEnumerable<string> EnumerateModuleDirs(string stackRoot)
    {
        var root = Root(stackRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Where(dir => File.Exists(Path.Combine(dir, ManifestFileName)));
    }

    public static List<(string ModuleId, string CsvPath)> CollectCsvSources(
        string stackRoot,
        IReadOnlyList<string> moduleOrder,
        string? tableFilter = null)
    {
        var result = new List<(string ModuleId, string CsvPath)>();
        var filter = tableFilter is null ? null : CsvNormalizer.NormalizeTableName(tableFilter);
        foreach (var moduleId in moduleOrder)
        {
            var csvDir = CsvDir(stackRoot, moduleId);
            if (!Directory.Exists(csvDir))
            {
                continue;
            }

            var manifest = LoadManifest(stackRoot, moduleId);
            foreach (var csv in Directory.EnumerateFiles(csvDir, "*.txt"))
            {
                var table = CsvNormalizer.NormalizeTableName(Path.GetFileName(csv));
                if (filter is not null && !string.Equals(table, filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ShouldSkipBaseTable(moduleId, table, manifest))
                {
                    continue;
                }

                result.Add((moduleId, csv));
            }
        }

        return result;
    }

    public static List<(string ModuleId, string CsvPath)> CollectCsvSources(
        string stackRoot,
        IReadOnlyList<string> moduleOrder,
        SessionBaseDbc sessionBase) =>
        CollectCsvSources(stackRoot, moduleOrder)
            .Where(source =>
                !string.Equals(source.ModuleId, sessionBase.ModuleId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    CsvNormalizer.NormalizeTableName(Path.GetFileName(source.CsvPath)),
                    sessionBase.TableName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static bool ShouldSkipBaseTable(string moduleId, string table, InstalledModuleManifest? manifest) =>
        manifest?.BaseDbc is { } baseDbc
        && string.Equals(baseDbc.TableName, table, StringComparison.OrdinalIgnoreCase)
        && string.Equals(baseDbc.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase);

    public static InstalledModuleManifest? LoadManifest(string stackRoot, string moduleId)
    {
        var path = ManifestPath(stackRoot, moduleId);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<InstalledModuleManifest>(File.ReadAllText(path), JsonOptions);
    }

    public static void SaveManifest(string stackRoot, string moduleId, InstalledModuleManifest manifest)
    {
        Directory.CreateDirectory(ModuleDir(stackRoot, moduleId));
        File.WriteAllText(ManifestPath(stackRoot, moduleId), JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public static SessionBaseDbc? FindBaseDbc(string stackRoot, IReadOnlyList<string> moduleOrder)
    {
        foreach (var moduleId in moduleOrder)
        {
            var manifest = LoadManifest(stackRoot, moduleId);
            if (manifest?.BaseDbc is not { } baseDbc)
            {
                continue;
            }

            var binary = Path.Combine(DbcDir(stackRoot, moduleId), $"{baseDbc.TableName}.dbc");
            if (File.Exists(binary))
            {
                return new SessionBaseDbc
                {
                    TableName = baseDbc.TableName,
                    ModuleId = moduleId,
                    BinaryPath = binary
                };
            }
        }

        return null;
    }
}

public sealed class InstalledModuleManifest
{
    public string ModuleId { get; set; } = string.Empty;
    public List<string> Tables { get; set; } = [];
    public Dictionary<string, List<string>> EntryIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Mpq { get; set; } = [];
    public List<string> Sql { get; set; } = [];
    public List<string> Addons { get; set; } = [];
    public List<string> Lua { get; set; } = [];
    public List<string> Maps { get; set; } = [];
    public InstalledModuleBaseDbc? BaseDbc { get; set; }
    public List<WorldserverConfHint> ConfHints { get; set; } = [];
}

public sealed class InstalledModuleBaseDbc
{
    public string TableName { get; set; } = string.Empty;
    public string ModuleId { get; set; } = string.Empty;
}
