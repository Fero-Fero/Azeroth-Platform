using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

public sealed record ProgressionPatchDefinition(
    int State,
    string Slug,
    string Expansion,
    string Index,
    string Title,
    string Description,
    bool IncrementsProgression);

/// <summary>Maps mod-individual-progression ProgressionState values to patch folder templates.</summary>
public static class ServerWideProgressionPatchCatalog
{
    /// <summary>Number of progression patches seeded for Server Wide Progression (Classic + TBC + WotLK).</summary>
    public const int ExpectedPatchCount = 18;

    private static readonly IReadOnlyList<ProgressionPatchDefinition> BuiltIn =
    [
        new(0, "START", "classic", "1.0", "Start", "Classic-era server progression baseline.", false),
        new(1, "MOLTEN_CORE", "classic", "1.1", "Molten Core", "Molten Core tier - Blackwing Lair becomes available.", true),
        new(2, "ONYXIA", "classic", "1.2", "Onyxia", "Onyxia tier progression.", true),
        new(3, "BLACKWING_LAIR", "classic", "1.3", "Blackwing Lair", "Blackwing Lair tier - ZG, AQ war effort, AQ quest line.", true),
        new(4, "PRE_AQ", "classic", "1.4", "Pre-AQ", "Pre-AQ gates progression.", true),
        new(5, "AQ_WAR", "classic", "1.5", "AQ War", "AQ war effort and outdoor war.", true),
        new(6, "AQ", "classic", "1.6", "AQ", "AQ raids and Scourge invasion lead-in.", true),
        new(7, "NAXX40", "classic", "1.7", "Naxxramas (40)", "Classic Naxxramas and Into the Breach.", true),
        new(8, "PRE_TBC", "tbc", "2.0", "Pre-TBC", "The Burning Crusade opens - Karazhan, Gruul, Magtheridon.", true),
        new(9, "TBC_TIER_1", "tbc", "2.1", "TBC Tier 1", "Serpentshrine Cavern and Tempest Keep.", true),
        new(10, "TBC_TIER_2", "tbc", "2.2", "TBC Tier 2", "Hyjal Summit and Black Temple.", true),
        new(12, "TBC_TIER_4", "tbc", "2.3", "TBC Tier 4", "Sunwell Plateau.", true),
        new(13, "TBC_TIER_5", "tbc", "2.4", "TBC Tier 5", "WotLK Naxx, Eye of Eternity, Obsidian Sanctum.", true),
        new(14, "WOTLK_TIER_1", "wotlk", "3.0", "WotLK Tier 1", "Ulduar.", true),
        new(15, "WOTLK_TIER_2", "wotlk", "3.1", "WotLK Tier 2", "Trial of the Crusader.", true),
        new(16, "WOTLK_TIER_3", "wotlk", "3.2", "WotLK Tier 3", "Icecrown Citadel.", true),
        new(17, "WOTLK_TIER_4", "wotlk", "3.3", "WotLK Tier 4", "Ruby Sanctum.", true),
        new(18, "WOTLK_TIER_5", "wotlk", "3.4", "WotLK Tier 5", "Final WotLK progression tier.", true),
    ];

    public static IReadOnlyList<ProgressionPatchDefinition> All => BuiltIn;

    public static ProgressionPatchDefinition? FindByState(int state) =>
        BuiltIn.FirstOrDefault(def => def.State == state);

    public static ProgressionPatchDefinition? FindByIndex(string index) =>
        BuiltIn.FirstOrDefault(def => string.Equals(def.Index, index, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<ProgressionPatchDefinition> ResolveDefinitions(string stackRoot)
    {
        var parsed = ServerWideProgressionHeaderParser.TryParseFromStack(stackRoot);
        if (parsed is null || parsed.Count == 0)
        {
            return BuiltIn;
        }

        var byState = BuiltIn.ToDictionary(def => def.State);
        var merged = new List<ProgressionPatchDefinition>();
        foreach (var entry in parsed.OrderBy(e => e.State))
        {
            if (byState.TryGetValue(entry.State, out var known))
            {
                merged.Add(known with { Slug = entry.Slug });
            }
        }

        return merged.Count > 0 ? merged : BuiltIn;
    }
}

internal static partial class ServerWideProgressionHeaderParser
{
    private const string HeaderFileName = "IndividualProgression.h";

    [GeneratedRegex(@"^\s*PROGRESSION_(\w+)\s*=\s*(\d+)\s*,?", RegexOptions.Multiline)]
    private static partial Regex ProgressionEntryRegex();

    public sealed record ParsedState(int State, string Slug);

    public static IReadOnlyList<ParsedState>? TryParseFromStack(string stackRoot)
    {
        foreach (var path in CandidateHeaderPaths(stackRoot))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var parsed = ParseHeader(File.ReadAllText(path));
            if (parsed.Count > 0)
            {
                return parsed;
            }
        }

        return null;
    }

    private static List<ParsedState> ParseHeader(string content)
    {
        var results = new List<ParsedState>();
        foreach (Match match in ProgressionEntryRegex().Matches(content))
        {
            if (match.Value.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var slug = match.Groups[1].Value;
            if (!int.TryParse(match.Groups[2].Value, out var state))
            {
                continue;
            }

            results.Add(new ParsedState(state, slug));
        }

        return results
            .GroupBy(entry => entry.State)
            .Select(group => group.First())
            .OrderBy(entry => entry.State)
            .ToList();
    }

    private static IEnumerable<string> CandidateHeaderPaths(string stackRoot)
    {
        yield return Path.Combine(stackRoot, "azerothcore-wotlk", "modules", "mod-individual-progression", "src", HeaderFileName);
        yield return Path.Combine(stackRoot, "modules", "mod-individual-progression", "src", HeaderFileName);
    }
}
