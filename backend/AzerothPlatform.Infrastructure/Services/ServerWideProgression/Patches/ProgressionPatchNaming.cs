using AzerothPlatform.Infrastructure.Services.Patches;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Derives stack patch folder names from Azeroth-Platform-Progression patch directory names
/// (for example <c>2.1 Serpentshrine Cavern</c> → <c>patch 2.1 Serpentshrine Cavern</c>).
/// </summary>
internal static class ProgressionPatchNaming
{
    public static bool TryFormatPatchKey(string repoPatchFolderName, out string patchKey)
    {
        patchKey = string.Empty;
        if (!TryParseRepoFolder(repoPatchFolderName, out var index, out var displayName))
        {
            return false;
        }

        patchKey = PatchFolderNames.Format(index, displayName);
        return true;
    }

    public static bool TryParseRepoFolder(
        string repoPatchFolderName,
        out PatchIndex index,
        out string? displayName)
    {
        index = default;
        displayName = null;
        var (indexPart, label) = SplitPatchSegment(repoPatchFolderName);
        if (!PatchIndex.TryParse(indexPart, out index, explicitSub1: true))
        {
            return false;
        }

        displayName = string.IsNullOrWhiteSpace(label)
            ? repoPatchFolderName.Trim()
            : label.Trim();
        return true;
    }

    public static int CountMissingRepoPatches(string repoDir, string stackRoot)
    {
        if (!Directory.Exists(repoDir))
        {
            return 0;
        }

        var missing = 0;
        foreach (var expansionDir in Directory.EnumerateDirectories(repoDir))
        {
            if (!IsKnownExpansion(Path.GetFileName(expansionDir)))
            {
                continue;
            }

            foreach (var referencePatchDir in Directory.EnumerateDirectories(expansionDir))
            {
                if (!TryFormatPatchKey(Path.GetFileName(referencePatchDir), out var patchKey))
                {
                    continue;
                }

                if (!Directory.Exists(MigrationLayout.PatchDir(stackRoot, patchKey)))
                {
                    missing++;
                }
            }
        }

        return missing;
    }

    public static string SlugFromLabel(string label) =>
        label.Trim()
            .ToUpperInvariant()
            .Replace(' ', '_')
            .Replace('-', '_');

    public static (string IndexPart, string? Label) SplitPatchSegment(string patchSegment)
    {
        var trimmed = patchSegment.Trim();
        var spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx < 0)
        {
            return (trimmed, null);
        }

        return (trimmed[..spaceIdx], trimmed[(spaceIdx + 1)..].Trim());
    }

    private static bool IsKnownExpansion(string expansionSegment) =>
        expansionSegment.Trim().ToLowerInvariant() is "classic" or "tbc" or "wotlk";
}
