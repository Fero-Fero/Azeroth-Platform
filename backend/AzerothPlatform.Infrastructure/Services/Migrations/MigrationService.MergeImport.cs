using System.IO.Compression;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.Migrations;

public sealed partial class MigrationService
{
    public async Task<MergePatchImportResultDto> MergePatchImportAsync(
        string stackId,
        string targetPatchKey,
        Stream? sqlArchive,
        Stream? clientArchive,
        CancellationToken cancellationToken = default)
    {
        if (sqlArchive is null && clientArchive is null)
        {
            throw new ArgumentException("At least one archive (sql or client) is required.");
        }

        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var patch = RequirePatch(stackRoot, targetPatchKey);

        if (patch.Level <= stack.AppliedPatchLevel)
        {
            throw new InvalidOperationException("Cannot merge content into an already-applied patch.");
        }

        var result = new MergePatchImportResultDto { TargetPatchKey = targetPatchKey };

        if (sqlArchive is not null)
        {
            result.SqlFiles += await MergeArchiveCategoriesAsync(
                stackRoot, targetPatchKey, sqlArchive, ["sql"], cancellationToken);
        }

        if (clientArchive is not null)
        {
            result.MpqFiles += await MergeArchiveCategoriesAsync(
                stackRoot, targetPatchKey, clientArchive, ["mpq", "dbc", "map"], cancellationToken);
        }

        return result;
    }

    private static async Task<int> MergeArchiveCategoriesAsync(
        string stackRoot,
        string targetPatchKey,
        Stream archive,
        string[] allowedTopCategories,
        CancellationToken cancellationToken)
    {
        var tempArchive = Path.Combine(Path.GetTempPath(), "azp-merge-" + Guid.NewGuid().ToString("N") + ".zip");
        var tempExtract = Path.Combine(Path.GetTempPath(), "azp-merge-extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using (var file = File.Create(tempArchive))
            {
                await archive.CopyToAsync(file, cancellationToken);
            }

            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(tempArchive, tempExtract);

            var copied = 0;
            foreach (var category in allowedTopCategories)
            {
                copied += CopyMergedCategory(tempExtract, stackRoot, targetPatchKey, category, cancellationToken);
            }

            if (copied == 0)
            {
                foreach (var category in allowedTopCategories)
                {
                    copied += CopyMergedCategoryFromNestedRoots(tempExtract, stackRoot, targetPatchKey, category, cancellationToken);
                }
            }

            return copied;
        }
        finally
        {
            try { if (File.Exists(tempArchive)) File.Delete(tempArchive); } catch { /* best effort */ }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { /* best effort */ }
        }
    }

    private static int CopyMergedCategoryFromNestedRoots(
        string extractRoot,
        string stackRoot,
        string targetPatchKey,
        string category,
        CancellationToken cancellationToken)
    {
        var copied = 0;
        foreach (var top in Directory.EnumerateDirectories(extractRoot))
        {
            copied += CopyMergedCategory(top, stackRoot, targetPatchKey, category, cancellationToken);
        }

        return copied;
    }

    private static int CopyMergedCategory(
        string extractRoot,
        string stackRoot,
        string targetPatchKey,
        string category,
        CancellationToken cancellationToken)
    {
        var sourceDir = Path.Combine(extractRoot, category);
        if (!Directory.Exists(sourceDir))
        {
            return 0;
        }

        var destRoot = category switch
        {
            "sql" => MigrationLayout.SqlDir(stackRoot, targetPatchKey),
            "dbc" => MigrationLayout.DbcDir(stackRoot, targetPatchKey),
            "map" => MigrationLayout.MapDir(stackRoot, targetPatchKey),
            "mpq" => MigrationLayout.MpqDir(stackRoot, targetPatchKey),
            _ => throw new ArgumentException($"Unsupported merge category: {category}")
        };

        if (category == "mpq")
        {
            return CopyMergedMpqCategory(sourceDir, stackRoot, targetPatchKey, destRoot, cancellationToken);
        }

        return CopyDirectoryRecursive(sourceDir, destRoot, cancellationToken);
    }

    private static int CopyMergedMpqCategory(
        string sourceDir,
        string stackRoot,
        string targetPatchKey,
        string destDir,
        CancellationToken cancellationToken)
    {
        var copied = 0;
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, file);
            if (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseMpqRemovalJson(File.ReadAllText(file), out var removals))
                {
                    AppendMpqRemovals(stackRoot, targetPatchKey, removals);
                    copied++;
                    continue;
                }

                continue;
            }

            var destination = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }

        return copied;
    }

    private static int CopyDirectoryRecursive(string sourceDir, string destDir, CancellationToken cancellationToken)
    {
        var copied = 0;
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }

        return copied;
    }
}
