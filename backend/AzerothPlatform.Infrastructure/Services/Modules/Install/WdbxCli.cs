using System.Diagnostics;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install;

public sealed class WdbxCli : IWdbxCli
{
    private readonly IMigrationImageService _imageService;
    private readonly MigrationOptions _migrationOptions;
    private readonly DockerOptions _dockerOptions;
    private readonly ILogger<WdbxCli> _logger;

    public WdbxCli(
        IMigrationImageService imageService,
        IOptions<MigrationOptions> migrationOptions,
        IOptions<DockerOptions> dockerOptions,
        ILogger<WdbxCli> logger)
    {
        _imageService = imageService;
        _migrationOptions = migrationOptions.Value;
        _dockerOptions = dockerOptions.Value;
        _logger = logger;
    }

    public async Task ExportDbcToCsvAsync(string dbcPath, string csvPath, CancellationToken cancellationToken = default)
    {
        var workDir = Path.GetDirectoryName(Path.GetFullPath(dbcPath))
            ?? throw new InvalidOperationException($"Invalid DBC path: {dbcPath}");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);

        var dbcName = Path.GetFileName(dbcPath);
        var requestedName = Path.GetFileName(csvPath);
        var exportName = WdbxExportFileName(requestedName);
        var stagedDbc = Path.Combine(workDir, dbcName);
        // workDir is the DBC's own directory, so this is a no-op unless the caller passed a DBC from
        // elsewhere. Copying a file onto itself throws IOException ("used by another process") on both
        // Windows and Linux, so the same-path case has to be skipped rather than left to overwrite.
        if (!SamePath(dbcPath, stagedDbc))
        {
            File.Copy(dbcPath, stagedDbc, overwrite: true);
        }

        await _imageService.EnsureWdbxImageAsync(cancellationToken);
        var args = $"-export -f {Quote(dbcName)} -b {_migrationOptions.WoWBuild} -o {Quote(exportName)}";
        var run = await RunAsync(workDir, args, cancellationToken);
        var produced = Path.Combine(workDir, exportName);
        if (!File.Exists(produced))
        {
            var detail = $"{run.StdErr} {run.StdOut}".Trim();
            if (IsMissingDefinitionError(run.StdOut, run.StdErr))
            {
                throw new WdbxDefinitionMissingException(dbcName, detail);
            }

            throw new InvalidOperationException(
                $"WDBXEditor export did not produce {exportName}. Exit {run.ExitCode}. {detail}");
        }

        if (!SamePath(produced, csvPath))
        {
            File.Copy(produced, csvPath, overwrite: true);
        }

        var text = await File.ReadAllTextAsync(csvPath, cancellationToken);
        await CsvNormalizer.WriteCrlfAsync(csvPath, text, cancellationToken);
    }

    public async Task ExtractDbcsFromMpqAsync(
        string mpqPath, string outputDir, string? filterName, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDir);
        var workDir = Path.GetDirectoryName(Path.GetFullPath(mpqPath))
            ?? throw new InvalidOperationException($"Invalid MPQ path: {mpqPath}");
        await _imageService.EnsureWdbxImageAsync(cancellationToken);

        var filter = string.IsNullOrWhiteSpace(filterName)
            ? "*.dbc"
            : $"{CsvNormalizer.NormalizeTableName(filterName)}.dbc";
        var outRel = "wdbx-extract";
        var stagedOut = Path.Combine(workDir, outRel);
        Directory.CreateDirectory(stagedOut);
        var args = $"-extract -s {Quote(Path.GetFileName(mpqPath))} -f {Quote(filter)} -o {Quote(outRel)}";
        (int ExitCode, string StdOut, string StdErr) run;
        try
        {
            run = await RunAsync(workDir, args, cancellationToken);
            foreach (var file in Directory.EnumerateFiles(stagedOut, "*.*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(outputDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }
        }
        finally
        {
            TryDelete(stagedOut);
        }

        if (run.ExitCode != 0 && !Directory.EnumerateFiles(outputDir, "*.dbc").Any())
        {
            throw new InvalidOperationException(
                $"WDBXEditor MPQ extract failed. Exit {run.ExitCode}. {run.StdErr} {run.StdOut}");
        }
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

    public async Task ImportCsvAsync(string dbcPath, string csvPath, CancellationToken cancellationToken = default)
    {
        var workDir = Path.GetDirectoryName(Path.GetFullPath(dbcPath))
            ?? throw new InvalidOperationException($"Invalid DBC path: {dbcPath}");
        Directory.CreateDirectory(workDir);
        var dbcName = Path.GetFileName(dbcPath);
        var csvName = Path.GetFileName(csvPath);
        var workCsv = Path.Combine(workDir, csvName);
        if (!SamePath(csvPath, workCsv))
        {
            File.Copy(csvPath, workCsv, overwrite: true);
        }

        var text = await File.ReadAllTextAsync(workCsv, cancellationToken);
        await CsvNormalizer.WriteCrlfAsync(workCsv, text, cancellationToken);

        await _imageService.EnsureWdbxImageAsync(cancellationToken);
        var args =
            $"-import -f {Quote(dbcName)} -b {_migrationOptions.WoWBuild} -c {Quote(csvName)} -h true -u Update -i TakeNewest";
        var run = await RunAsync(workDir, args, cancellationToken);
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"WDBXEditor import of {csvName} into {dbcName} failed. Exit {run.ExitCode}. {run.StdErr} {run.StdOut}");
        }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string hostWorkDir, string toolArgs, CancellationToken cancellationToken)
    {
        var (mountArgs, workDir) = ResolveLocalToolMount(hostWorkDir);
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"run --rm --memory 4g --memory-swap 4g {mountArgs} -w {workDir} {_migrationOptions.WdbxImage} {toolArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogDebug("WDBX {Args}", startInfo.Arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start docker process for WDBXEditor.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private (string MountArgs, string WorkDir) ResolveLocalToolMount(string hostWorkDir)
    {
        var dataMount = GetManagerDataMountPath();
        if (TryGetDataVolumeSubpath(hostWorkDir, dataMount, out var relative)
            && !string.IsNullOrWhiteSpace(_dockerOptions.DataVolumeName))
        {
            var containerWork = string.IsNullOrEmpty(relative) ? "/data" : $"/data/{relative}";
            return ($"-v {_dockerOptions.DataVolumeName}:/data", containerWork);
        }

        return ($"-v \"{hostWorkDir}\":/w", "/w");
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

    private static bool TryGetDataVolumeSubpath(string localSourceDir, string dataMount, out string relative)
    {
        relative = string.Empty;
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

    internal static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// WDBXEditor's <c>-o</c> path is both the output file and the format: it only accepts
    /// <c>.csv</c>, <c>.json</c>, or <c>.sql</c>. The platform stores DBC dumps as <c>.txt</c>
    /// (WoW client convention), so export always uses a sibling <c>.csv</c> name.
    /// </summary>
    internal static string WdbxExportFileName(string requestedFileName) =>
        Path.ChangeExtension(Path.GetFileName(requestedFileName), ".csv");

    /// <summary>
    /// WDBXEditor only exports tables that have a definition XML for the client build.
    /// WotLK 3.3.5a (12340) client-data still ships leftovers such as <c>CharVariations.dbc</c>
    /// (Classic/TBC only).
    /// </summary>
    internal static bool IsMissingDefinitionError(string? stdout, string? stderr) =>
        $"{stderr} {stdout}".Contains("Could not find definition", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

/// <summary>Thrown when WDBXEditor has no DBC definition for the requested table and build.</summary>
public sealed class WdbxDefinitionMissingException : InvalidOperationException
{
    public WdbxDefinitionMissingException(string table, string? detail)
        : base(string.IsNullOrWhiteSpace(detail)
            ? $"WDBXEditor has no definition for {table}."
            : $"WDBXEditor has no definition for {table}. {detail}")
    {
        Table = table;
    }

    public string Table { get; }
}
