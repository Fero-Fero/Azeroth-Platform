using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.Patches;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Creates stack patch folders from the on-disk layout of Azeroth-Platform-Progression.
/// </summary>
internal static class ProgressionRepoPatchSeeder
{
    private const string ProgressionMetadataFileName = "progression.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static int Seed(
        string repoDir,
        string stackRoot,
        bool onlyMissing,
        ICollection<string>? createdPatchKeys = null)
    {
        if (!Directory.Exists(repoDir))
        {
            return 0;
        }

        var headerStates = ServerWideProgressionHeaderParser.TryParseFromStack(stackRoot);
        var catalog = ServerWideProgressionPatchCatalog.All;
        var created = 0;

        foreach (var expansionDir in Directory.EnumerateDirectories(repoDir))
        {
            if (!TryNormalizeExpansion(Path.GetFileName(expansionDir), out var expansion))
            {
                continue;
            }

            foreach (var referencePatchDir in Directory.EnumerateDirectories(expansionDir))
            {
                var patchFolderName = Path.GetFileName(referencePatchDir);
                if (!TryResolvePatchKey(expansion, patchFolderName, catalog, out var patchKey, out var definition))
                {
                    continue;
                }

                var stackPatchDir = MigrationLayout.PatchDir(stackRoot, patchKey);
                if (onlyMissing
                    && File.Exists(Path.Combine(stackPatchDir, ProgressionMetadataFileName)))
                {
                    continue;
                }

                MigrationLayout.EnsurePatchDirectories(stackRoot, patchKey);
                CopyDescription(referencePatchDir, stackPatchDir);
                WriteProgressionMetadata(
                    stackPatchDir,
                    definition,
                    headerStates,
                    expansion,
                    patchFolderName,
                    patchKey);
                created++;
                createdPatchKeys?.Add(patchKey);
            }
        }

        return created;
    }

    private static bool TryResolvePatchKey(
        string expansion,
        string patchFolderName,
        IReadOnlyList<ProgressionPatchDefinition> catalog,
        out string patchKey,
        out ProgressionPatchDefinition? definition)
    {
        definition = CatalogDefinitionForMetadata(expansion, patchFolderName, catalog);
        return ProgressionPatchNaming.TryFormatPatchKey(patchFolderName, out patchKey);
    }

    private static ProgressionPatchDefinition? CatalogDefinitionForMetadata(
        string expansion,
        string patchFolderName,
        IReadOnlyList<ProgressionPatchDefinition> catalog)
    {
        var definition = ProgressionPatchFolderResolver.MatchDefinition(expansion, patchFolderName, catalog);
        if (definition is null)
        {
            return null;
        }

        var (_, label) = ProgressionPatchNaming.SplitPatchSegment(patchFolderName);
        if (string.IsNullOrWhiteSpace(label))
        {
            return definition;
        }

        // Index-only matches (2.0 Karazhan vs catalog 2.0 PRE_TBC) must not inherit Express metadata.
        if (LabelsAgree(definition.Title, label) || LabelsAgree(definition.Slug.Replace('_', ' '), label))
        {
            return definition;
        }

        return null;
    }

    private static bool LabelsAgree(string catalog, string repo)
    {
        var a = catalog.Trim();
        var b = repo.Trim();
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase)
            || a.Contains(b, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDescription(string referencePatchDir, string stackPatchDir)
    {
        foreach (var descriptionFile in MigrationLayout.PatchDescriptionFileNames)
        {
            var source = Path.Combine(referencePatchDir, descriptionFile);
            if (!File.Exists(source))
            {
                continue;
            }

            File.Copy(source, Path.Combine(stackPatchDir, descriptionFile), overwrite: true);
            return;
        }
    }

    private static void WriteProgressionMetadata(
        string stackPatchDir,
        ProgressionPatchDefinition? definition,
        IReadOnlyList<ServerWideProgressionHeaderParser.ParsedState>? headerStates,
        string expansion,
        string repoPatchFolderName,
        string patchKey)
    {
        var slug = definition?.Slug
            ?? (ProgressionPatchNaming.TryParseRepoFolder(repoPatchFolderName, out _, out var label) && label is not null
                ? ProgressionPatchNaming.SlugFromLabel(label)
                : ExtractSlugFromPatchKey(patchKey));
        var metadata = definition is not null
            ? new PatchProgressionMetadataDto
            {
                State = definition.State,
                Slug = definition.Slug,
                Expansion = definition.Expansion,
                IncrementsProgression = definition.IncrementsProgression,
            }
            : BuildMetadataFromHeader(slug, expansion, headerStates);

        File.WriteAllText(
            Path.Combine(stackPatchDir, ProgressionMetadataFileName),
            JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static PatchProgressionMetadataDto BuildMetadataFromHeader(
        string slug,
        string expansion,
        IReadOnlyList<ServerWideProgressionHeaderParser.ParsedState>? headerStates)
    {
        var headerMatch = headerStates?.FirstOrDefault(entry =>
            string.Equals(entry.Slug, slug, StringComparison.OrdinalIgnoreCase));

        if (headerMatch is not null)
        {
            return new PatchProgressionMetadataDto
            {
                State = headerMatch.State,
                Slug = headerMatch.Slug,
                Expansion = expansion,
                IncrementsProgression = headerMatch.State != 0,
            };
        }

        return new PatchProgressionMetadataDto
        {
            State = 0,
            Slug = slug,
            Expansion = expansion,
            IncrementsProgression = !string.Equals(slug, "START", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string ExtractSlugFromPatchKey(string patchKey)
    {
        if (!PatchFolderNames.TryParse(patchKey, out _, out var slug) || string.IsNullOrWhiteSpace(slug))
        {
            return patchKey;
        }

        return slug;
    }

    private static bool TryNormalizeExpansion(string expansionSegment, out string expansion)
    {
        expansion = expansionSegment.Trim().ToLowerInvariant() switch
        {
            "classic" => "classic",
            "tbc" => "tbc",
            "wotlk" => "wotlk",
            _ => string.Empty,
        };

        return expansion.Length > 0;
    }
}
