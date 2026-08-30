using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

public sealed class ModuleInstallHelpers : IModuleInstallHelpers
{
    private readonly string _moduleId;
    private readonly string _packageRoot;
    private readonly ModuleInstallSession _session;
    private readonly IWdbxCli _wdbx;
    private readonly IDbcBaselineStore _dbcStore;
    private readonly IMpqToolCli _mpq;
    private readonly string? _baselineDbcDir;
    private readonly ModuleInstallContribution _contribution = new();

    public ModuleInstallHelpers(
        string moduleId,
        string packageRoot,
        ModuleInstallSession session,
        IWdbxCli wdbx,
        IDbcBaselineStore dbcStore,
        IMpqToolCli mpq,
        string? baselineDbcDir = null)
    {
        _moduleId = moduleId;
        _packageRoot = packageRoot;
        _session = session;
        _wdbx = wdbx;
        _dbcStore = dbcStore;
        _mpq = mpq;
        _baselineDbcDir = baselineDbcDir;
    }

    public ModuleInstallContribution Contribution => _contribution;

    public Task ExtractArchive(string relativeArchivePath, CancellationToken cancellationToken = default)
    {
        var source = ResolvePackagePath(relativeArchivePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Archive not found in module '{_moduleId}': {relativeArchivePath}", source);
        }

        var dest = Path.Combine(_session.ModuleDir(_moduleId), "extracted", ArchiveStem(relativeArchivePath));
        Directory.CreateDirectory(dest);
        ArchiveExtractor.Extract(source, dest, cancellationToken);
        StripSingleWrapperFolder(dest);
        return Task.CompletedTask;
    }

    public async Task ExtractAllDbcs(CancellationToken cancellationToken = default) =>
        await ExportDbcsAsync(FindDbcs(null), cancellationToken);

    public async Task ExtractDbcByName(string name, CancellationToken cancellationToken = default)
    {
        var matches = FindDbcs(name);
        if (matches.Count == 0)
        {
            throw new FileNotFoundException(
                $"DBC '{name}' was not found under the extracted files for module '{_moduleId}'.");
        }

        await ExportDbcsAsync(matches, cancellationToken);
    }

    public async Task ExtractDbcsFromMpq(string mpqPath, string? name = null, CancellationToken cancellationToken = default)
    {
        var mpq = ResolveExtractedOrPackage(mpqPath);
        if (!File.Exists(mpq))
        {
            throw new FileNotFoundException($"MPQ not found in module '{_moduleId}': {mpqPath}", mpq);
        }

        var outDir = Path.Combine(_session.ModuleDir(_moduleId), "extracted", "mpq-dbc");
        Directory.CreateDirectory(outDir);
        await _wdbx.ExtractDbcsFromMpqAsync(mpq, outDir, name, cancellationToken);
        var filter = string.IsNullOrWhiteSpace(name) ? null : name;
        var dbcs = Directory.EnumerateFiles(outDir, "*.dbc", SearchOption.AllDirectories)
            .Where(path => filter is null || NamesMatch(path, filter))
            .ToList();
        if (dbcs.Count == 0)
        {
            throw new FileNotFoundException(
                $"No DBC files were extracted from '{mpqPath}'" + (filter is null ? "." : $" matching '{filter}'."));
        }

        await ExportDbcsAsync(dbcs, cancellationToken);
    }

    public void SetAsBaseDBC(string name)
    {
        var table = CsvNormalizer.NormalizeTableName(name);
        var binary = FindDbcs(table).FirstOrDefault()
            ?? throw new FileNotFoundException(
                $"SetAsBaseDBC(\"{name}\") requires {table}.dbc to already be extracted for module '{_moduleId}'.");

        _session.SetBaseDbc(new SessionBaseDbc
        {
            TableName = table,
            ModuleId = _moduleId,
            BinaryPath = binary
        });

        _contribution.Artifacts.Add(new ModuleInstallArtifact
        {
            Kind = ModuleInstallArtifactKind.DbcBase,
            SourcePath = binary,
            DestHint = table
        });
    }

    public async Task TrimAllDbcs(CancellationToken cancellationToken = default)
    {
        var csvDir = Path.Combine(_session.ModuleDir(_moduleId), "csv");
        if (!Directory.Exists(csvDir))
        {
            return;
        }

        var baseTable = _session.BaseDbc is { } b
            && string.Equals(b.ModuleId, _moduleId, StringComparison.OrdinalIgnoreCase)
            ? b.TableName
            : null;

        foreach (var csv in Directory.EnumerateFiles(csvDir, "*.txt"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = CsvNormalizer.NormalizeTableName(Path.GetFileName(csv));
            if (baseTable is not null && string.Equals(table, baseTable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseline = await EnsureBaselineCsvAsync(table, cancellationToken);
            if (baseline is null)
            {
                continue;
            }

            var kept = await DbcTrimHelper.TrimAsync(csv, baseline, cancellationToken);
            if (kept)
            {
                AddUniqueArtifact(ModuleInstallArtifactKind.DbcCsv, csv, table);
            }
        }
    }

    public Task IncludeSql(string relativePath, CancellationToken cancellationToken = default)
    {
        var source = ResolvePackagePath(relativePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"SQL file not found in module '{_moduleId}': {relativePath}", source);
        }

        var kind = InferSqlKind(relativePath);
        var destDir = Path.Combine(_session.ModuleDir(_moduleId), "sql", SqlFolder(kind));
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, Path.GetFileName(source));
        File.Copy(source, dest, overwrite: true);
        _contribution.Artifacts.Add(new ModuleInstallArtifact
        {
            Kind = kind,
            SourcePath = dest,
            DestHint = Path.GetFileName(source)
        });
        return Task.CompletedTask;
    }

    public Task IncludeMpq(string relativePath, CancellationToken cancellationToken = default)
    {
        var source = ResolveExtractedOrPackage(relativePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"MPQ not found in module '{_moduleId}': {relativePath}", source);
        }

        var destDir = Path.Combine(_session.ModuleDir(_moduleId), "mpq");
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, Path.GetFileName(source));
        File.Copy(source, dest, overwrite: true);
        _contribution.Artifacts.Add(new ModuleInstallArtifact
        {
            Kind = ModuleInstallArtifactKind.Mpq,
            SourcePath = dest,
            DestHint = Path.GetFileName(source)
        });
        return Task.CompletedTask;
    }

    public async Task IncludeCsv(string relativePath, CancellationToken cancellationToken = default)
    {
        var source = ResolvePackagePath(relativePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"CSV file not found in module '{_moduleId}': {relativePath}", source);
        }

        await CopyCsvFileAsync(source, cancellationToken);
    }

    public async Task IncludeCsvDirectory(string relativeDir, CancellationToken cancellationToken = default)
    {
        var source = ResolvePackagePath(relativeDir);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"CSV folder not found in module '{_moduleId}': {relativeDir}");
        }

        var files = Directory.EnumerateFiles(source)
            .Where(path =>
                path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            throw new FileNotFoundException($"No CSV files found in module '{_moduleId}': {relativeDir}");
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CopyCsvFileAsync(file, cancellationToken);
        }
    }

    public async Task PackMpqDirectory(string relativeDir, string mpqFileName, CancellationToken cancellationToken = default)
    {
        var source = ResolvePackagePath(relativeDir);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"MPQ source folder not found in module '{_moduleId}': {relativeDir}");
        }

        var name = Path.GetFileName(mpqFileName);
        if (string.IsNullOrWhiteSpace(name)
            || !string.Equals(name, mpqFileName, StringComparison.Ordinal)
            || !name.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid overlay MPQ file name: {mpqFileName}", nameof(mpqFileName));
        }

        if (!Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException(
                $"No files to pack into {name} from module '{_moduleId}' path '{relativeDir}'.");
        }

        var staging = Path.Combine(_session.ModuleDir(_moduleId), "extracted", "mpq-pack");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        CopyDirectory(source, staging, cancellationToken);
        var destDir = Path.Combine(_session.ModuleDir(_moduleId), "mpq");
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, name);
        await _mpq.PackPreservePathsAsync(staging, dest, cancellationToken);
        _contribution.Artifacts.Add(new ModuleInstallArtifact
        {
            Kind = ModuleInstallArtifactKind.Mpq,
            SourcePath = dest,
            DestHint = name
        });
    }

    public Task IncludeMaps(string relativeDir, CancellationToken cancellationToken = default)
    {
        var source = ResolvePackagePath(relativeDir);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Map folder not found in module '{_moduleId}': {relativeDir}");
        }

        var destRoot = Path.Combine(_session.ModuleDir(_moduleId), "maps");
        var copied = 0;
        foreach (var sub in InstalledModulesLayout.DataVolumeSubdirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = Path.Combine(source, sub);
            if (!Directory.Exists(child))
            {
                continue;
            }

            var dest = Path.Combine(destRoot, sub);
            CopyDirectory(child, dest, cancellationToken);
            _contribution.Artifacts.Add(new ModuleInstallArtifact
            {
                Kind = ModuleInstallArtifactKind.Maps,
                SourcePath = dest,
                DestHint = sub
            });
            copied++;
        }

        if (copied == 0)
        {
            throw new DirectoryNotFoundException(
                $"No maps, mmaps, or vmaps folder found in module '{_moduleId}': {relativeDir}");
        }

        return Task.CompletedTask;
    }

    public Task IncludeAddon(string relativeDir, string folderName, CancellationToken cancellationToken = default)
    {
        var source = ResolvePackagePath(relativeDir);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Addon folder not found in module '{_moduleId}': {relativeDir}");
        }

        var dest = Path.Combine(_session.ModuleDir(_moduleId), "other", folderName);
        CopyDirectory(source, dest, cancellationToken);
        _contribution.Artifacts.Add(new ModuleInstallArtifact
        {
            Kind = ModuleInstallArtifactKind.Addon,
            SourcePath = dest,
            DestHint = folderName
        });
        return Task.CompletedTask;
    }

    public Task IncludeLua(string relativePath, string destRelativePath, CancellationToken cancellationToken = default)
    {
        var source = ResolvePackagePath(relativePath);
        var destRelative = SanitizeLuaDest(destRelativePath);
        var luaRoot = Path.Combine(_session.ModuleDir(_moduleId), "lua");

        if (Directory.Exists(source))
        {
            var dest = string.IsNullOrEmpty(destRelative) ? luaRoot : Path.Combine(luaRoot, destRelative);
            CopyDirectory(source, dest, cancellationToken);
            _contribution.Artifacts.Add(new ModuleInstallArtifact
            {
                Kind = ModuleInstallArtifactKind.Lua,
                SourcePath = dest,
                DestHint = destRelative
            });
            return Task.CompletedTask;
        }

        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Lua path not found in module '{_moduleId}': {relativePath}", source);
        }

        var fileDestRelative = string.IsNullOrEmpty(destRelative) ? Path.GetFileName(source) : destRelative;
        var fileDest = Path.Combine(luaRoot, fileDestRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(fileDest)!);
        File.Copy(source, fileDest, overwrite: true);
        _contribution.Artifacts.Add(new ModuleInstallArtifact
        {
            Kind = ModuleInstallArtifactKind.Lua,
            SourcePath = fileDest,
            DestHint = fileDestRelative.Replace('\\', '/')
        });
        return Task.CompletedTask;
    }

    public void AddConfHint(string key, string value) =>
        _contribution.ConfHints.Add(new WorldserverConfHint { Key = key, Value = value });

    private async Task ExportDbcsAsync(IReadOnlyList<string> dbcPaths, CancellationToken cancellationToken)
    {
        var moduleRoot = _session.ModuleDir(_moduleId);
        var dbcDir = Path.Combine(moduleRoot, "dbc");
        var csvDir = Path.Combine(moduleRoot, "csv");
        Directory.CreateDirectory(dbcDir);
        Directory.CreateDirectory(csvDir);
        foreach (var dbc in dbcPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = CsvNormalizer.NormalizeTableName(Path.GetFileName(dbc));
            var destDbc = Path.Combine(dbcDir, $"{table}.dbc");
            if (!string.Equals(Path.GetFullPath(dbc), Path.GetFullPath(destDbc), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(dbc, destDbc, overwrite: true);
            }

            var csv = Path.Combine(csvDir, CsvNormalizer.TableFileName(table));
            await _wdbx.ExportDbcToCsvAsync(destDbc, csv, cancellationToken);
            AddUniqueArtifact(ModuleInstallArtifactKind.DbcCsv, csv, table);
        }
    }

    private IReadOnlyList<string> FindDbcs(string? name)
    {
        var root = _session.ModuleDir(_moduleId);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*.dbc", SearchOption.AllDirectories)
            .OrderBy(path => path.Contains($"{Path.DirectorySeparatorChar}extracted{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Where(path => name is null || NamesMatch(path, name))
            .ToList();
    }

    private async Task<string?> EnsureBaselineCsvAsync(string table, CancellationToken cancellationToken)
    {
        if (_session.BaseDbc is { } baseDbc
            && string.Equals(baseDbc.TableName, table, StringComparison.OrdinalIgnoreCase))
        {
            var csv = Path.Combine(_session.ModuleDir(baseDbc.ModuleId), "csv", CsvNormalizer.TableFileName(table));
            if (File.Exists(csv))
            {
                return csv;
            }
        }

        if (!string.IsNullOrWhiteSpace(_baselineDbcDir))
        {
            var dbc = Path.Combine(_baselineDbcDir, $"{table}.dbc");
            if (File.Exists(dbc))
            {
                return await _dbcStore.EnsureTableCsvAsync(table, dbc, cancellationToken);
            }
        }

        return _dbcStore.FindTableCsv(table);
    }

    private string ResolvePackagePath(string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(_packageRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(_packageRoot).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, Path.GetFullPath(_packageRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes the module package: {relative}");
        }

        return combined;
    }

    private string ResolveExtractedOrPackage(string relative)
    {
        var extractedRoot = Path.Combine(_session.ModuleDir(_moduleId), "extracted");
        if (Directory.Exists(extractedRoot))
        {
            var name = Path.GetFileName(relative);
            var match = Directory.EnumerateFiles(extractedRoot, name, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return ResolvePackagePath(relative);
    }

    private void AddUniqueArtifact(ModuleInstallArtifactKind kind, string path, string? destHint)
    {
        if (_contribution.Artifacts.Any(artifact =>
                artifact.Kind == kind
                && string.Equals(artifact.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _contribution.Artifacts.Add(new ModuleInstallArtifact
        {
            Kind = kind,
            SourcePath = path,
            DestHint = destHint
        });
    }

    private static bool NamesMatch(string path, string name)
    {
        var table = CsvNormalizer.NormalizeTableName(name);
        return string.Equals(CsvNormalizer.NormalizeTableName(Path.GetFileName(path)), table, StringComparison.OrdinalIgnoreCase);
    }

    private static string ArchiveStem(string relative)
    {
        var name = Path.GetFileName(relative);
        foreach (var ext in new[] { ".7z", ".zip", ".rar", ".tar.gz", ".tgz", ".tar" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^ext.Length];
            }
        }

        return Path.GetFileNameWithoutExtension(name);
    }

    private static void StripSingleWrapperFolder(string dest)
    {
        var dirs = Directory.GetDirectories(dest);
        var files = Directory.GetFiles(dest);
        if (files.Length == 0 && dirs.Length == 1)
        {
            var wrapper = dirs[0];
            foreach (var entry in Directory.GetFileSystemEntries(wrapper))
            {
                var target = Path.Combine(dest, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    CopyDirectory(entry, target, CancellationToken.None);
                }
                else
                {
                    File.Copy(entry, target, overwrite: true);
                }
            }

            Directory.Delete(wrapper, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string dest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static ModuleInstallArtifactKind InferSqlKind(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Contains("/auth/", StringComparison.OrdinalIgnoreCase))
        {
            return ModuleInstallArtifactKind.SqlAuth;
        }

        if (normalized.Contains("/characters/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/character/", StringComparison.OrdinalIgnoreCase))
        {
            return ModuleInstallArtifactKind.SqlCharacters;
        }

        return ModuleInstallArtifactKind.SqlWorld;
    }

    private static string SqlFolder(ModuleInstallArtifactKind kind) => kind switch
    {
        ModuleInstallArtifactKind.SqlAuth => "auth",
        ModuleInstallArtifactKind.SqlCharacters => "characters",
        _ => "world"
    };

    private static string SanitizeLuaDest(string destRelativePath)
    {
        var dest = (destRelativePath ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (dest.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(dest))
        {
            throw new ArgumentException($"Invalid Lua destination: {destRelativePath}");
        }

        return dest.Replace('/', Path.DirectorySeparatorChar);
    }

    private async Task CopyCsvFileAsync(string source, CancellationToken cancellationToken)
    {
        var table = CsvNormalizer.NormalizeTableName(Path.GetFileName(source));
        var baseline = _dbcStore.FindTableCsv(table);
        if (baseline is not null)
        {
            table = CsvNormalizer.NormalizeTableName(Path.GetFileName(baseline));
        }

        var csvDir = Path.Combine(_session.ModuleDir(_moduleId), "csv");
        Directory.CreateDirectory(csvDir);
        var dest = Path.Combine(csvDir, CsvNormalizer.TableFileName(table));
        var text = await File.ReadAllTextAsync(source, cancellationToken);
        await CsvNormalizer.WriteCrlfAsync(dest, text, cancellationToken);
        AddUniqueArtifact(ModuleInstallArtifactKind.DbcCsv, dest, table);
    }
}
