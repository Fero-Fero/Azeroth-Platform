using System.Text.RegularExpressions;
using AzerothPlatform.Infrastructure.Services.Migrations;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Maps Azeroth-Platform-Progression destination paths (expansion + patch folder + category)
/// to stack <c>migrations/</c> directories. Repo folders often use WoW client version numbers
/// (for example <c>3.5 Ruby Sanctum</c>) while stack folders use progression catalog indices
/// (for example <c>patch 3.3 WOTLK_TIER_4</c>).
/// </summary>
internal static partial class ProgressionPatchFolderResolver
{
    private const string ModuleSourcePrefix = "mod-individual-progression/";

    /// <summary>Strips a redundant module prefix from mapping.json source paths.</summary>
    public static string NormalizeModuleSourcePath(string source)
    {
        if (source.StartsWith(ModuleSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return source[ModuleSourcePrefix.Length..];
        }

        return source;
    }

    /// <summary>
    /// Resolves a destination such as <c>Classic/1.2 Onyxia/sql/world/</c> to an absolute stack path.
    /// Returns null when no matching progression patch exists on the stack.
    /// </summary>
    public static string? Resolve(string stackRoot, string destinationPath)
    {
        var parts = destinationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var expansionSegment = parts[0];
        var patchSegment = parts[1];
        if (!TryNormalizeExpansion(expansionSegment, out _))
        {
            return null;
        }

        if (ProgressionPatchNaming.TryFormatPatchKey(patchSegment, out var patchKey))
        {
            var repoPatchDir = MigrationLayout.PatchDir(stackRoot, patchKey);
            if (Directory.Exists(repoPatchDir))
            {
                return parts.Length <= 2
                    ? repoPatchDir
                    : Path.Combine(repoPatchDir, Path.Combine(parts[2..]));
            }
        }

        if (!TryNormalizeExpansion(expansionSegment, out var expansion))
        {
            return null;
        }

        var catalog = ServerWideProgressionPatchCatalog.ResolveDefinitions(stackRoot);
        var definition = MatchDefinition(expansion, patchSegment, catalog);
        if (definition is null)
        {
            return null;
        }

        if (!PatchIndex.TryParse(definition.Index, out var index, explicitSub1: true))
        {
            return null;
        }

        var patchDir = MigrationLayout.PatchDir(stackRoot, PatchFolderNames.Format(index, definition.Slug));
        if (!Directory.Exists(patchDir))
        {
            // Fall back to any stack folder with the same catalog index (custom slug).
            patchDir = FindStackPatchDirByIndex(stackRoot, index) ?? patchDir;
        }

        if (!Directory.Exists(patchDir))
        {
            return null;
        }

        if (parts.Length <= 2)
        {
            return patchDir;
        }

        return Path.Combine(patchDir, Path.Combine(parts[2..]));
    }

    internal static ProgressionPatchDefinition? MatchDefinition(
        string expansion,
        string patchSegment,
        IReadOnlyList<ProgressionPatchDefinition> catalog)
    {
        var (indexPart, label) = SplitPatchSegment(patchSegment);

        if (!string.IsNullOrWhiteSpace(indexPart))
        {
            var byIndex = catalog.FirstOrDefault(def =>
                string.Equals(def.Expansion, expansion, StringComparison.OrdinalIgnoreCase)
                && string.Equals(def.Index, indexPart, StringComparison.OrdinalIgnoreCase));
            if (byIndex is not null)
            {
                return byIndex;
            }
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var normalizedLabel = NormalizeMatchText(label);
        if (normalizedLabel.Length == 0)
        {
            return null;
        }

        var expansionMatches = catalog
            .Where(def => string.Equals(def.Expansion, expansion, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var labelMatches = expansionMatches
            .Where(def => DefinitionMatchesLabel(def, normalizedLabel))
            .ToList();

        if (labelMatches.Count == 1)
        {
            return labelMatches[0];
        }

        if (labelMatches.Count > 1)
        {
            // Prefer the closest index when multiple definitions share wording (e.g. tier titles).
            if (PatchIndex.TryParse(indexPart, out var repoIndex, explicitSub1: true))
            {
                return labelMatches
                    .OrderBy(def => IndexDistance(def.Index, repoIndex))
                    .First();
            }

            return labelMatches[0];
        }

        return null;
    }

    private static (string IndexPart, string? Label) SplitPatchSegment(string patchSegment) =>
        ProgressionPatchNaming.SplitPatchSegment(patchSegment);

    private static bool TryNormalizeExpansion(string expansionSegment, out string expansion)
    {
        expansion = expansionSegment.Trim().ToLowerInvariant() switch
        {
            "classic" => "classic",
            "tbc" => "tbc",
            "wotlk" => "wotlk",
            "custom" => "custom",
            _ => string.Empty,
        };

        return expansion.Length > 0;
    }

    private static string? FindStackPatchDirByIndex(string stackRoot, PatchIndex targetIndex)
    {
        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        if (!Directory.Exists(migrationsRoot))
        {
            return null;
        }

        foreach (var dir in Directory.EnumerateDirectories(migrationsRoot))
        {
            if (PatchFolderNames.TryParse(Path.GetFileName(dir), out var folderIndex, out _)
                && folderIndex.Equals(targetIndex))
            {
                return dir;
            }
        }

        return null;
    }

    private static bool DefinitionMatchesLabel(ProgressionPatchDefinition definition, string normalizedRepoLabel)
    {
        foreach (var candidate in new[]
                 {
                     definition.Title,
                     definition.Description,
                     definition.Slug.Replace('_', ' '),
                 })
        {
            var normalizedCandidate = NormalizeMatchText(candidate);
            if (normalizedCandidate.Length == 0)
            {
                continue;
            }

            if (normalizedCandidate.Contains(normalizedRepoLabel, StringComparison.Ordinal)
                || normalizedRepoLabel.Contains(normalizedCandidate, StringComparison.Ordinal))
            {
                return true;
            }

            var repoWords = normalizedRepoLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (repoWords.Length == 0)
            {
                continue;
            }

            var significantWords = repoWords.Where(word => word.Length > 2).ToList();
            if (significantWords.Count == 0)
            {
                continue;
            }

            var matchedWords = significantWords.Count(word =>
                normalizedCandidate.Contains(word, StringComparison.Ordinal));
            if (matchedWords >= Math.Min(2, significantWords.Count))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexDistance(string catalogIndex, PatchIndex repoIndex)
    {
        if (!PatchIndex.TryParse(catalogIndex, out var catalogPatchIndex, explicitSub1: true))
        {
            return int.MaxValue;
        }

        return Math.Abs(catalogPatchIndex.ToEncodedLevel() - repoIndex.ToEncodedLevel());
    }

    private static string NormalizeMatchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return NormalizeWhitespaceRegex().Replace(text.ToLowerInvariant(), " ").Trim(' ', '.');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex NormalizeWhitespaceRegex();
}
