using AzerothPlatform.Infrastructure.Services.Migrations;

namespace AzerothPlatform.Infrastructure.Services.IndividualProgression;

/// <summary>
/// Validates stack patch folders against the on-disk layout of Azeroth-Platform-Progression.
/// </summary>
public static class ProgressionRepoStructureValidator
{
    private static readonly HashSet<string> ReferencePatchCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "script", "sql", "dbc", "map", "mpq",
    };

    /// <summary>
    /// Resolves the local Azeroth-Platform-Progression directory. Uses <paramref name="configuredPath"/>
    /// when set, otherwise walks up from the current directory to find the platform repo and checks the
    /// sibling <c>../Azeroth-Platform-Progression</c> folder.
    /// </summary>
    public static string? ResolveLocalRepoPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var full = Path.GetFullPath(configuredPath.Trim());
            return Directory.Exists(full) ? full : null;
        }

        var dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            if (IsPlatformRoot(dir))
            {
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent is null)
                {
                    return null;
                }

                var sibling = Path.Combine(parent, "Azeroth-Platform-Progression");
                return Directory.Exists(sibling) ? sibling : null;
            }

            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        return null;
    }

    /// <summary>
    /// Compares each catalog progression patch on the stack to the matching folder in the reference repo.
    /// </summary>
    public static void Validate(string stackRoot, string repoRoot, ICollection<string> errors)
    {
        if (!Directory.Exists(repoRoot))
        {
            errors.Add($"Azeroth-Platform-Progression repository not found at {repoRoot}.");
            return;
        }

        ValidateRepoRootLayout(repoRoot, errors);

        foreach (var definition in IndividualProgressionPatchCatalog.All)
        {
            var referencePatchDir = FindReferencePatchDir(repoRoot, definition);
            if (referencePatchDir is null)
            {
                errors.Add(
                    $"Reference patch missing in Azeroth-Platform-Progression: {FormatExpansionFolder(definition.Expansion)}/{definition.Index} ({definition.Title}).");
                continue;
            }

            if (!TryFindStackPatchDir(stackRoot, definition, out var stackPatchDir, out var stackPatchKey))
            {
                continue;
            }

            ValidatePatchStructure(
                referencePatchDir,
                stackPatchDir,
                stackPatchKey,
                errors);
        }
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
        foreach (var expansion in new[] { "classic", "tbc", "wotlk" })
        {
            if (FindExpansionDir(repoRoot, expansion) is null)
            {
                errors.Add($"Azeroth-Platform-Progression is missing expansion folder: {FormatExpansionFolder(expansion)}.");
            }
        }
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

            if (!ReferencePatchCategories.Contains(categoryName))
            {
                errors.Add(
                    $"{stackPatchKey}: reference patch '{referenceName}' has unsupported folder '{categoryName}'.");
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

    private static string? FindReferencePatchDir(string repoRoot, ProgressionPatchDefinition definition)
    {
        var expansionDir = FindExpansionDir(repoRoot, definition.Expansion);
        if (expansionDir is null)
        {
            return null;
        }

        foreach (var patchDir in Directory.EnumerateDirectories(expansionDir))
        {
            if (!TryParseReferencePatchIndex(Path.GetFileName(patchDir), out var index))
            {
                continue;
            }

            if (index.Equals(definition.Index, StringComparison.OrdinalIgnoreCase))
            {
                return patchDir;
            }
        }

        return null;
    }

    private static bool TryFindStackPatchDir(
        string stackRoot,
        ProgressionPatchDefinition definition,
        out string stackPatchDir,
        out string stackPatchKey)
    {
        stackPatchDir = string.Empty;
        stackPatchKey = string.Empty;

        if (!PatchIndex.TryParse(definition.Index, out var targetIndex, explicitSub1: true))
        {
            return false;
        }

        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        if (!Directory.Exists(migrationsRoot))
        {
            return false;
        }

        foreach (var dir in Directory.EnumerateDirectories(migrationsRoot))
        {
            var folderName = Path.GetFileName(dir);
            if (!PatchFolderNames.TryParse(folderName, out var folderIndex, out _))
            {
                continue;
            }

            if (!folderIndex.Equals(targetIndex))
            {
                continue;
            }

            stackPatchKey = folderName;
            stackPatchDir = dir;
            return true;
        }

        return false;
    }

    private static string? FindExpansionDir(string repoRoot, string expansion)
    {
        if (!Directory.Exists(repoRoot))
        {
            return null;
        }

        return Directory.EnumerateDirectories(repoRoot)
            .FirstOrDefault(dir => string.Equals(
                Path.GetFileName(dir),
                FormatExpansionFolder(expansion),
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseReferencePatchIndex(string folderName, out string index)
    {
        index = string.Empty;
        var spaceIdx = folderName.IndexOf(' ');
        var indexPart = spaceIdx >= 0 ? folderName[..spaceIdx] : folderName;
        if (!PatchIndex.TryParse(indexPart, out _, explicitSub1: true))
        {
            return false;
        }

        index = indexPart;
        return true;
    }

    private static string FormatExpansionFolder(string expansion) => expansion.ToLowerInvariant() switch
    {
        "classic" => "Classic",
        "tbc" => "Tbc",
        "wotlk" => "Wotlk",
        _ => expansion,
    };

    private static bool IsPlatformRoot(string dir) =>
        File.Exists(Path.Combine(dir, "progression_plan.md"))
        || Directory.Exists(Path.Combine(dir, "backend", "AzerothPlatform.Api"));
}
