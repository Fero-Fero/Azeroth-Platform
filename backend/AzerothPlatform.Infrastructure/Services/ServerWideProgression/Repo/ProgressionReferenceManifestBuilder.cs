using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.Migrations;

namespace AzerothPlatform.Infrastructure.Services.IndividualProgression;

/// <summary>
/// Captures the Azeroth-Platform-Progression layout during sync for later validation.
/// </summary>
internal static class ProgressionReferenceManifestBuilder
{
    public static ProgressionReferenceManifestDto BuildFromRepo(string repoRoot)
    {
        var manifest = new ProgressionReferenceManifestDto
        {
            CapturedAt = DateTimeOffset.UtcNow,
        };

        if (!Directory.Exists(repoRoot))
        {
            return manifest;
        }

        foreach (var expansionDir in Directory.EnumerateDirectories(repoRoot))
        {
            if (!IsKnownExpansion(Path.GetFileName(expansionDir)))
            {
                continue;
            }

            foreach (var referencePatchDir in Directory.EnumerateDirectories(expansionDir))
            {
                if (!ProgressionPatchNaming.TryFormatPatchKey(
                        Path.GetFileName(referencePatchDir),
                        out var stackPatchKey))
                {
                    continue;
                }

                manifest.ExpectedPatchKeys.Add(stackPatchKey);
                manifest.RequiredFilesByPatchKey[stackPatchKey] = CollectRequiredFiles(referencePatchDir);
            }
        }

        return manifest;
    }

    private static List<string> CollectRequiredFiles(string referencePatchDir)
    {
        var files = new List<string>();
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
            var stackRelativePath = ProgressionRepoStructureValidator.NormalizeRepoCategoryPath(relativeToPatch);
            if (string.IsNullOrEmpty(stackRelativePath))
            {
                continue;
            }

            files.Add(stackRelativePath);
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsKnownExpansion(string expansionSegment) =>
        expansionSegment.Trim().ToLowerInvariant() is "classic" or "tbc" or "wotlk";
}
