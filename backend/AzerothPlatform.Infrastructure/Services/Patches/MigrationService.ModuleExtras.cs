using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.Modules.Install;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;

namespace AzerothPlatform.Infrastructure.Services.Patches;

public sealed partial class MigrationService
{
    public async Task<bool> TryEnsureServerDbcBaselineAsync(
        string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        await ResolveEngineContextAsync(stack, cancellationToken);
        try
        {
            await ExtractServerDbcFromVolumeAsync(stack, stackRoot, cancellationToken);
            return Directory.EnumerateFiles(MigrationLayout.ServerDbcDir(stackRoot), "*.dbc").Any();
        }
        catch (InvalidOperationException)
        {
            return IsBaselineInitialized(stackRoot);
        }
    }

    public async Task PushServerDbcFilesAsync(
        string stackId,
        IReadOnlyList<string> dbcFileNames,
        CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        await ResolveEngineContextAsync(stack, cancellationToken);
        var result = new ApplyPatchResultDto();
        await PushServerDbcToVolumeAsync(stack, stackRoot, dbcFileNames, result, cancellationToken);
    }

    public async Task RebuildPatchDAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        await ResolveEngineContextAsync(stack, cancellationToken);
        var result = new ApplyPatchResultDto();
        await BuildPatchDAsync(stack, stackRoot, result, cancellationToken);
    }

    public async Task ApplySqlFilesAsync(
        string stackId,
        string database,
        IReadOnlyList<string> sqlFilePaths,
        CancellationToken cancellationToken = default)
    {
        if (sqlFilePaths.Count == 0)
        {
            return;
        }

        var stack = await GetStackAsync(stackId, cancellationToken);
        await ResolveEngineContextAsync(stack, cancellationToken);
        var container = DbContainer(stackId, stack.StackName);
        await using var stream = OpenCombinedTransactionalSqlStream(sqlFilePaths);
        var args =
            $"{_engineContextArg}exec -i {container} mysql --disable-local-infile -uroot -p{stack.DatabaseRootPassword} {database}";
        var run = await RunProcessAsync("docker", args, workingDirectory: null, stdin: stream, cancellationToken);
        if (run.ExitCode != 0)
        {
            var error = FilterMySqlWarnings(run.StdErr);
            throw new InvalidOperationException(
                $"Module extra-data SQL failed against {database} (rolled back): {error}");
        }
    }

    public async Task PublishOverlayMpqAsync(
        string stackId, string mpqPath, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        await ResolveEngineContextAsync(stack, cancellationToken);
        if (!File.Exists(mpqPath))
        {
            throw new FileNotFoundException("Overlay MPQ not found.", mpqPath);
        }

        var overlayDataDir = MigrationLayout.ClientOverlayDataDir(stackRoot);
        Directory.CreateDirectory(overlayDataDir);
        File.Copy(mpqPath, Path.Combine(overlayDataDir, Path.GetFileName(mpqPath)), overwrite: true);
        var result = new ApplyPatchResultDto();
        await PushOverlayToEngineAsync(stack, stackRoot, result, cancellationToken);
        await RescanClientContainerAsync(stack, result, cancellationToken);
    }

    public async Task PublishDataVolumeFilesAsync(
        string stackId,
        string volumeSubdir,
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken = default)
    {
        if (!InstalledModulesLayout.DataVolumeSubdirs.Contains(volumeSubdir, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Data volume subdir '{volumeSubdir}' is not allowed. Expected maps, mmaps, or vmaps.",
                nameof(volumeSubdir));
        }

        if (sourceFiles.Count == 0)
        {
            return;
        }

        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        await ResolveEngineContextAsync(stack, cancellationToken);
        await SeedDataVolumeSubdirAsync(stack, stackRoot, volumeSubdir, sourceFiles, cancellationToken);
    }

    internal static string ApplyConfHints(string content, IEnumerable<WorldserverConfHint> hints)
    {
        var updated = content;
        foreach (var hint in hints)
        {
            updated = ServerConfigValueEditor.SetValue(updated, hint.Key, hint.Value);
        }

        return updated;
    }
}
