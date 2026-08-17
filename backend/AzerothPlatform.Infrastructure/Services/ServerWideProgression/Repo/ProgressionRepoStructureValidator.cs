using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.Migrations;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Validates stack patch folders against the on-disk layout of Azeroth-Platform-Progression.
/// </summary>
public static class ProgressionRepoStructureValidator
{
    /// <summary>
    /// Compares each progression patch folder in the reference repo to the matching stack patch.
    /// The reference repo lives on the stack at <see cref="MigrationLayout.ProgressionRepoDir"/>.
    /// </summary>
    public static void Validate(string stackRoot, string repoRoot, ICollection<string> errors)
    {
        if (!Directory.Exists(repoRoot))
        {
            errors.Add($"Azeroth-Platform-Progression repository not found at {repoRoot}.");
            return;
        }

        ValidateRepoRootLayout(repoRoot, errors);

        foreach (var (expansionName, expansionDir) in EnumerateExpansionDirectories(repoRoot))
        {
            foreach (var referencePatchDir in Directory.EnumerateDirectories(expansionDir))
            {
                var patchFolderName = Path.GetFileName(referencePatchDir);
                if (!ProgressionPatchNaming.TryFormatPatchKey(patchFolderName, out var stackPatchKey))
                {
                    errors.Add(
                        $"Reference patch {expansionName}/{patchFolderName} has an invalid folder name (expected '<index> <label>').");
                    continue;
                }

                var stackPatchDir = MigrationLayout.PatchDir(stackRoot, stackPatchKey);
                if (!Directory.Exists(stackPatchDir))
                {
                    errors.Add(
                        $"No stack patch matches reference patch {expansionName}/{patchFolderName}. Expected {stackPatchKey}. Run progression sync to create patch folders from Azeroth-Platform-Progression.");
                    continue;
                }

                ValidatePatchStructure(
                    referencePatchDir,
                    stackPatchDir,
                    stackPatchKey,
                    errors);
            }
        }
    }

    /// <summary>
    /// Validates stack patch folders against a persisted reference manifest from the last progression sync.
    /// </summary>
    public static void ValidateAgainstManifest(
        string stackRoot,
        ProgressionReferenceManifestDto manifest,
        ICollection<string> errors)
    {
        if (manifest.ExpectedPatchKeys.Count == 0)
        {
            errors.Add(
                "Progression reference manifest is empty. Run Update & re-sync to refresh the expected patch layout.");
            return;
        }

        foreach (var patchKey in manifest.ExpectedPatchKeys)
        {
            var stackPatchDir = MigrationLayout.PatchDir(stackRoot, patchKey);
            if (!Directory.Exists(stackPatchDir))
            {
                errors.Add(
                    $"No stack patch matches reference patch {patchKey}. Run progression sync to create patch folders from Azeroth-Platform-Progression.");
                continue;
            }

            if (!manifest.RequiredFilesByPatchKey.TryGetValue(patchKey, out var requiredFiles))
            {
                continue;
            }

            foreach (var stackRelativePath in requiredFiles)
            {
                var stackFile = Path.Combine(
                    stackPatchDir,
                    stackRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(stackFile))
                {
                    errors.Add($"{patchKey}: missing '{stackRelativePath}' (required by Azeroth-Platform-Progression).");
                }
            }

            if (!HasPatchDescription(stackPatchDir))
            {
                errors.Add($"{patchKey}: missing patch description file.");
            }
        }
    }

    /// <summary>Counts parseable patch folders under Classic/Tbc/Wotlk in the reference repository.</summary>
    public static int CountReferencePatches(string repoRoot) =>
        ProgressionRepoAlignment.CountExpectedPatches(repoRoot);

    /// <summary>Counts stack patch folders that contain progression metadata.</summary>
    public static int CountManagedProgressionPatches(string stackRoot) =>
        CountManagedProgressionPatchesInternal(stackRoot);

    private static int CountManagedProgressionPatchesInternal(string stackRoot)
    {
        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        if (!Directory.Exists(migrationsRoot))
        {
            return 0;
        }

        return Directory.EnumerateDirectories(migrationsRoot)
            .Count(dir => File.Exists(Path.Combine(dir, "progression.json")));
    }

    /// <summary>
    /// Maps repo-relative category paths to stack patch sub-paths (e.g. sql/character → sql/characters).
    /// </summary>
    public static string NormalizeRepoCategoryPath(string? categoryPath)
    {
        if (string.IsNullOrWhiteSpace(categoryPath))
        {
            return string.Empty;
        }

        var normalized = categoryPath.Replace('\\', '/').Trim('/');
        if (normalized.Equals("sql/character", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("sql/character/", StringComparison.OrdinalIgnoreCase))
        {
            return "sql/characters" + normalized["sql/character".Length..];
        }

        return normalized;
    }

    private static void ValidateRepoRootLayout(string repoRoot, ICollection<string> errors)
    {
        if (EnumerateExpansionDirectories(repoRoot).Count == 0)
        {
            errors.Add(
                "Azeroth-Platform-Progression has no recognized expansion folders (expected Classic, Tbc, or Wotlk).");
        }
    }

    private static List<(string ExpansionName, string ExpansionDir)> EnumerateExpansionDirectories(string repoRoot)
    {
        var results = new List<(string, string)>();
        if (!Directory.Exists(repoRoot))
        {
            return results;
        }

        foreach (var expansionDir in Directory.EnumerateDirectories(repoRoot))
        {
            var expansionName = Path.GetFileName(expansionDir);
            if (expansionName.StartsWith('.')
                || string.Equals(expansionName, ".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryNormalizeExpansionName(expansionName, out _))
            {
                results.Add((expansionName, expansionDir));
            }
        }

        return results;
    }

    private static bool TryNormalizeExpansionName(string expansionName, out string expansionKey)
    {
        expansionKey = expansionName.Trim().ToLowerInvariant() switch
        {
            "classic" => "classic",
            "tbc" => "tbc",
            "wotlk" => "wotlk",
            _ => string.Empty,
        };

        return expansionKey.Length > 0;
    }

    private static void ValidatePatchStructure(
        string referencePatchDir,
        string stackPatchDir,
        string stackPatchKey,
        ICollection<string> errors)
    {
        var referenceName = Path.GetFileName(referencePatchDir);

        foreach (var fileName in Directory.EnumerateFiles(referencePatchDir))
        {
            if (Path.GetFileName(fileName).StartsWith('.'))
            {
                continue;
            }

            if (!MigrationLayout.IsPatchDescriptionFile(Path.GetFileName(fileName)))
            {
                continue;
            }

            if (!HasPatchDescription(stackPatchDir))
            {
                errors.Add(
                    $"{stackPatchKey}: missing {Path.GetFileName(fileName)} (expected patch description file).");
            }
        }

        foreach (var referenceCategoryDir in Directory.EnumerateDirectories(referencePatchDir))
        {
            var categoryName = Path.GetFileName(referenceCategoryDir);
            if (categoryName.StartsWith('.'))
            {
                continue;
            }

            if (categoryName.Equals("sql", StringComparison.OrdinalIgnoreCase))
            {
                ValidateSqlStructure(referencePatchDir, stackPatchDir, stackPatchKey, errors);
                continue;
            }

            if (!DirectoryHasContent(referenceCategoryDir))
            {
                continue;
            }

            var stackCategoryDir = Path.Combine(stackPatchDir, categoryName);
            if (!Directory.Exists(stackCategoryDir))
            {
                errors.Add($"{stackPatchKey}: missing '{categoryName}/' directory (required by reference patch '{referenceName}').");
                continue;
            }

            if (categoryName.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                ValidateConfigFiles(referenceCategoryDir, stackCategoryDir, stackPatchKey, referenceName, errors);
            }
        }

        ValidateReferenceFiles(referencePatchDir, stackPatchDir, stackPatchKey, referenceName, errors);
    }

    private static void ValidateReferenceFiles(
        string referencePatchDir,
        string stackPatchDir,
        string stackPatchKey,
        string referenceName,
        ICollection<string> errors)
    {
        foreach (var referenceFile in Directory.EnumerateFiles(referencePatchDir, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(referenceFile);
            if (fileName.StartsWith('.'))
            {
                continue;
            }

            if (MigrationLayout.IsPatchDescriptionFile(fileName))
            {
                continue;
            }

            var relativeToPatch = Path.GetRelativePath(referencePatchDir, referenceFile)
                .Replace(Path.DirectorySeparatorChar, '/');
            var stackRelativePath = NormalizeRepoCategoryPath(relativeToPatch);
            if (string.IsNullOrEmpty(stackRelativePath))
            {
                continue;
            }

            var stackFile = Path.Combine(stackPatchDir, stackRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(stackFile))
            {
                errors.Add(
                    $"{stackPatchKey}: missing '{stackRelativePath}' (required by reference patch '{referenceName}').");
            }
        }
    }

    private static void ValidateSqlStructure(
        string referencePatchDir,
        string stackPatchDir,
        string stackPatchKey,
        ICollection<string> errors)
    {
        var referenceSqlDir = Path.Combine(referencePatchDir, "sql");
        var stackSqlDir = Path.Combine(stackPatchDir, "sql");
        if (!Directory.Exists(stackSqlDir))
        {
            errors.Add($"{stackPatchKey}: missing 'sql/' directory.");
            return;
        }

        foreach (var referenceDatabaseDir in Directory.EnumerateDirectories(referenceSqlDir))
        {
            var databaseName = Path.GetFileName(referenceDatabaseDir);
            if (databaseName.StartsWith('.'))
            {
                continue;
            }

            if (!DirectoryHasContent(referenceDatabaseDir))
            {
                continue;
            }

            var stackDatabaseName = MapSqlDatabaseName(databaseName);
            var stackDatabaseDir = Path.Combine(stackSqlDir, stackDatabaseName);
            if (!Directory.Exists(stackDatabaseDir))
            {
                errors.Add(
                    $"{stackPatchKey}: missing 'sql/{stackDatabaseName}/' (reference uses 'sql/{databaseName}/').");
            }
        }
    }

    private static void ValidateConfigFiles(
        string referenceConfigDir,
        string stackConfigDir,
        string stackPatchKey,
        string referenceName,
        ICollection<string> errors)
    {
        foreach (var referenceFile in Directory.EnumerateFiles(referenceConfigDir, "*.json"))
        {
            var fileName = Path.GetFileName(referenceFile);
            var stackFile = Path.Combine(stackConfigDir, fileName);
            if (!File.Exists(stackFile))
            {
                errors.Add(
                    $"{stackPatchKey}: missing config/{fileName} (required by reference patch '{referenceName}').");
            }
        }
    }

    private static string MapSqlDatabaseName(string referenceDatabaseName) =>
        referenceDatabaseName.Equals("character", StringComparison.OrdinalIgnoreCase)
            ? "characters"
            : referenceDatabaseName;

    private static bool DirectoryHasContent(string dir) =>
        Directory.EnumerateFileSystemEntries(dir)
            .Any(entry => !Path.GetFileName(entry).StartsWith('.'));

    private static bool HasPatchDescription(string stackPatchDir)
    {
        foreach (var name in MigrationLayout.PatchDescriptionFileNames)
        {
            if (File.Exists(Path.Combine(stackPatchDir, name)))
            {
                return true;
            }
        }

        return false;
    }
}
