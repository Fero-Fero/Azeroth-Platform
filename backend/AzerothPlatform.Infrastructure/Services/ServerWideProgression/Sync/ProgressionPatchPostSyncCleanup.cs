using AzerothPlatform.Infrastructure.Services.Patches;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// After progression sync has copied files into patch folders, extract leftover archives and
/// drop same-folder duplicate names so zips are not left beside the files they unpacked.
/// </summary>
public static class ProgressionPatchPostSyncCleanup
{
    private const string ProgressionMetadataFileName = "progression.json";

    private static readonly string[] ArchiveSuffixes =
    [
        ".tar.gz",
        ".tar.bz2",
        ".tar.xz",
        ".tgz",
        ".tbz2",
        ".7z",
        ".zip",
        ".rar",
        ".tar",
    ];

    public readonly record struct Result(int ArchivesExtracted, int DuplicateFilesRemoved)
    {
        public int TotalRemoved => ArchivesExtracted + DuplicateFilesRemoved;
    }

    public static Result Run(string stackRoot, ICollection<string> log)
    {
        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        if (!Directory.Exists(migrationsRoot))
        {
            return default;
        }

        var extracted = 0;
        var duplicates = 0;

        foreach (var patchDir in Directory.EnumerateDirectories(migrationsRoot))
        {
            if (!File.Exists(Path.Combine(patchDir, ProgressionMetadataFileName)))
            {
                continue;
            }

            var extractedThisPatch = 0;
            for (var pass = 0; pass < 3; pass++)
            {
                var extractedThisPass = ExtractArchivesInPatch(patchDir, log);
                extractedThisPatch += extractedThisPass;
                if (extractedThisPass == 0)
                {
                    break;
                }
            }

            extracted += extractedThisPatch;
            duplicates += RemoveSameFolderDuplicateNames(patchDir, log);
        }

        if (extracted > 0 || duplicates > 0)
        {
            log.Add(
                $"Cleaned patch folders after sync: extracted and removed {extracted} archive(s), removed {duplicates} duplicate file(s).");
        }

        return new Result(extracted, duplicates);
    }

    private static int ExtractArchivesInPatch(string patchDir, ICollection<string> log)
    {
        var archives = Directory.EnumerateFiles(patchDir, "*", SearchOption.AllDirectories)
            .Where(IsArchiveFile)
            .ToList();

        var extracted = 0;
        foreach (var archivePath in archives)
        {
            var dest = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrEmpty(dest))
            {
                continue;
            }

            try
            {
                ArchiveExtractor.Extract(archivePath, dest);
                StripSingleWrapperFolder(dest);
                File.Delete(archivePath);
                extracted++;
                log.Add($"Extracted and removed archive '{Path.GetFileName(archivePath)}' in {RelativePatchPath(patchDir, dest)}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Add($"Left archive '{Path.GetFileName(archivePath)}' in place: {ex.Message}");
            }
        }

        return extracted;
    }

    private static int RemoveSameFolderDuplicateNames(string patchDir, ICollection<string> log)
    {
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(patchDir, "*", SearchOption.AllDirectories)
                     .Prepend(patchDir))
        {
            var groups = Directory.EnumerateFiles(directory)
                .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);

            foreach (var group in groups)
            {
                var keep = group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First();
                foreach (var duplicate in group.Where(path =>
                             !string.Equals(path, keep, StringComparison.Ordinal)))
                {
                    try
                    {
                        File.Delete(duplicate);
                        removed++;
                        log.Add($"Removed duplicate '{Path.GetFileName(duplicate)}' next to {Path.GetFileName(keep)}.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.Add($"Could not remove duplicate '{Path.GetFileName(duplicate)}': {ex.Message}");
                    }
                }
            }
        }

        return removed;
    }

    private static bool IsArchiveFile(string path)
    {
        var name = Path.GetFileName(path);
        return ArchiveSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> PatchCategoryFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "sql", "dbc", "map", "mpq", "config", "lua", "news"
    };

    private static void StripSingleWrapperFolder(string dest)
    {
        var wrappers = Directory.GetDirectories(dest)
            .Where(dir => !PatchCategoryFolders.Contains(Path.GetFileName(dir)))
            .ToList();
        if (wrappers.Count != 1)
        {
            return;
        }

        MergeDirectory(wrappers[0], dest);
    }

    private static void MergeDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var directory in Directory.GetDirectories(source))
        {
            MergeDirectory(directory, Path.Combine(dest, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.GetFiles(source))
        {
            if (IsArchiveFile(file))
            {
                continue;
            }

            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }

        Directory.Delete(source, recursive: true);
    }

    private static string RelativePatchPath(string patchDir, string dest)
    {
        var relative = Path.GetRelativePath(patchDir, dest).Replace('\\', '/');
        return string.IsNullOrEmpty(relative) || relative == "." ? Path.GetFileName(patchDir) : relative;
    }
}
