using AzerothPlatform.Infrastructure.Services.Patches;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Aligns stack patch folders with the Azeroth-Platform-Progression repository layout.
/// </summary>
internal static class ProgressionRepoAlignment
{
    private const string ProgressionMetadataFileName = "progression.json";

    public static HashSet<string> EnumerateExpectedPatchKeys(string repoDir)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(repoDir))
        {
            return keys;
        }

        foreach (var expansionDir in Directory.EnumerateDirectories(repoDir))
        {
            if (!IsKnownExpansion(Path.GetFileName(expansionDir)))
            {
                continue;
            }

            foreach (var referencePatchDir in Directory.EnumerateDirectories(expansionDir))
            {
                if (ProgressionPatchNaming.TryFormatPatchKey(
                        Path.GetFileName(referencePatchDir),
                        out var patchKey))
                {
                    keys.Add(patchKey);
                }
            }
        }

        return keys;
    }

    public static int CountExpectedPatches(string repoDir) =>
        EnumerateExpectedPatchKeys(repoDir).Count;

    public static int CountAlignedPatches(string repoDir, string stackRoot)
    {
        var count = 0;
        foreach (var patchKey in EnumerateExpectedPatchKeys(repoDir))
        {
            if (Directory.Exists(MigrationLayout.PatchDir(stackRoot, patchKey)))
            {
                count++;
            }
        }

        return count;
    }

    public static int CountMissingPatches(string repoDir, string stackRoot) =>
        ProgressionPatchNaming.CountMissingRepoPatches(repoDir, stackRoot);

    public static int CountExpectedPatches(IReadOnlyCollection<string> expectedPatchKeys) =>
        expectedPatchKeys.Count;

    public static int CountAlignedPatches(IReadOnlyCollection<string> expectedPatchKeys, string stackRoot)
    {
        var count = 0;
        foreach (var patchKey in expectedPatchKeys)
        {
            if (Directory.Exists(MigrationLayout.PatchDir(stackRoot, patchKey)))
            {
                count++;
            }
        }

        return count;
    }

    public static int CountMissingPatches(IReadOnlyCollection<string> expectedPatchKeys, string stackRoot)
    {
        var missing = 0;
        foreach (var patchKey in expectedPatchKeys)
        {
            if (!Directory.Exists(MigrationLayout.PatchDir(stackRoot, patchKey)))
            {
                missing++;
            }
        }

        return missing;
    }

    /// <summary>
    /// Reports managed progression patches on the stack that are not present in the repository layout.
    /// </summary>
    public static void ValidateUnexpectedManagedPatches(
        IReadOnlySet<string> expectedPatchKeys,
        string stackRoot,
        ICollection<string> errors)
    {
        ValidatePatchFolderAlignment(expectedPatchKeys, stackRoot, errors);
    }

    /// <summary>
    /// Ensures stack patch folders exactly match the synced Azeroth-Platform-Progression reference:
    /// every expected folder must exist with the correct name, and no extra classic/tbc/wotlk patch
    /// folders or managed progression folders may remain under <c>migrations/</c>.
    /// </summary>
    public static void ValidatePatchFolderAlignment(
        IReadOnlyCollection<string> expectedPatchKeys,
        string stackRoot,
        ICollection<string> errors)
    {
        if (expectedPatchKeys.Count == 0)
        {
            return;
        }

        var expectedSet = expectedPatchKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        if (!Directory.Exists(migrationsRoot))
        {
            foreach (var expectedKey in expectedPatchKeys)
            {
                errors.Add(
                    $"Missing progression patch folder '{expectedKey}'. Run Update & re-sync to create patch folders from Azeroth-Platform-Progression.");
            }

            return;
        }

        var stackPatchDirs = Directory.EnumerateDirectories(migrationsRoot).ToList();
        var stackKeysByIndex = new Dictionary<PatchIndex, List<string>>();
        foreach (var patchDir in stackPatchDirs)
        {
            var patchKey = Path.GetFileName(patchDir);
            if (!PatchFolderNames.TryParse(patchKey, out var index, out _))
            {
                continue;
            }

            if (!stackKeysByIndex.TryGetValue(index, out var keys))
            {
                keys = [];
                stackKeysByIndex[index] = keys;
            }

            keys.Add(patchKey);
        }

        foreach (var expectedKey in expectedPatchKeys)
        {
            var expectedDir = MigrationLayout.PatchDir(stackRoot, expectedKey);
            if (Directory.Exists(expectedDir))
            {
                continue;
            }

            errors.Add(
                $"Missing progression patch folder '{expectedKey}'. Run Update & re-sync to create patch folders from Azeroth-Platform-Progression.");

            if (!PatchFolderNames.TryParse(expectedKey, out var expectedIndex, out _))
            {
                continue;
            }

            if (!stackKeysByIndex.TryGetValue(expectedIndex, out var stackKeysWithIndex))
            {
                continue;
            }

            foreach (var misnamedKey in stackKeysWithIndex.Where(key =>
                         !expectedSet.Contains(key)))
            {
                errors.Add(
                    $"Patch index {expectedIndex.ToIndexString()} is named '{misnamedKey}' but Azeroth-Platform-Progression expects '{expectedKey}'. Run Update & re-sync.");
            }
        }

        foreach (var patchDir in stackPatchDirs)
        {
            var patchKey = Path.GetFileName(patchDir);
            if (expectedSet.Contains(patchKey))
            {
                continue;
            }

            var hasProgressionMetadata = File.Exists(Path.Combine(patchDir, ProgressionMetadataFileName));
            var isProgressionExpansionPatch = PatchFolderNames.TryParse(patchKey, out var index, out _)
                && index.ExpansionRoot is >= 1 and <= 3;

            if (!hasProgressionMetadata && !isProgressionExpansionPatch)
            {
                continue;
            }

            errors.Add(
                $"Unexpected progression patch folder '{patchKey}' is not in Azeroth-Platform-Progression. Run Update & re-sync or remove the orphaned patch folder.");
        }
    }

    /// <summary>
    /// Removes managed progression patch folders that no longer exist in the repository layout.
    /// </summary>
    public static int RemoveOrphanedManagedPatches(
        string repoDir,
        string stackRoot,
        ICollection<string>? log = null)
    {
        var expected = EnumerateExpectedPatchKeys(repoDir);
        if (expected.Count == 0)
        {
            return 0;
        }

        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        if (!Directory.Exists(migrationsRoot))
        {
            return 0;
        }

        var removed = 0;
        foreach (var patchDir in Directory.EnumerateDirectories(migrationsRoot))
        {
            var patchKey = Path.GetFileName(patchDir);
            if (expected.Contains(patchKey))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(patchDir, ProgressionMetadataFileName)))
            {
                continue;
            }

            try
            {
                Directory.Delete(patchDir, recursive: true);
                removed++;
                log?.Add($"Removed orphaned progression patch folder '{patchKey}' (not in Azeroth-Platform-Progression layout).");
            }
            catch (Exception ex)
            {
                log?.Add($"Failed to remove orphaned progression patch '{patchKey}': {ex.Message}");
            }
        }

        return removed;
    }

    private static bool IsKnownExpansion(string expansionSegment) =>
        expansionSegment.Trim().ToLowerInvariant() is "classic" or "tbc" or "wotlk";
}
