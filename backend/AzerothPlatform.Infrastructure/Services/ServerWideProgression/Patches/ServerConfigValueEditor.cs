using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>Line-based read/write for AzerothCore <c>Key = Value</c> config files.</summary>
internal static partial class ServerConfigValueEditor
{
    [GeneratedRegex(@"^\s*#", RegexOptions.Compiled)]
    private static partial Regex CommentLineRegex();

    public static bool TryGetValue(string content, string key, out string value)
    {
        value = string.Empty;
        foreach (var line in content.Split('\n'))
        {
            if (CommentLineRegex().IsMatch(line))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var lineKey = line[..eq].Trim();
            if (!string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = line[(eq + 1)..].Trim();
            return true;
        }

        return false;
    }

    public static string SetValue(string content, string key, string value)
    {
        var lines = content.Split('\n').ToList();
        var replaced = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (CommentLineRegex().IsMatch(line))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var lineKey = line[..eq].Trim();
            if (!string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines[i] = $"{key} = {value}";
            replaced = true;
            break;
        }

        if (!replaced)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"{key} = {value}");
        }

        return string.Join('\n', lines);
    }

    public static Dictionary<string, string> GrepIndividualProgressionKeys(string content)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n'))
        {
            if (CommentLineRegex().IsMatch(line))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            if (!key.StartsWith("IndividualProgression.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results[key] = line[(eq + 1)..].Trim();
        }

        return results;
    }

    public static void ApplyKeyMapping(
        IndividualProgressionKeyNames keys,
        Dictionary<string, string> discovered)
    {
        keys.StartingProgression = MatchKey(discovered, "StartingProgression") ?? keys.StartingProgression;
        keys.ProgressionLimit = MatchKey(discovered, "ProgressionLimit") ?? keys.ProgressionLimit;
        keys.TbcRacesUnlockProgression = MatchKey(discovered, "TbcRaces", "Unlock") ?? keys.TbcRacesUnlockProgression;
        keys.TbcRacesStartingProgression = MatchKey(discovered, "TbcRaces", "Starting") ?? keys.TbcRacesStartingProgression;
    }

    private static string? MatchKey(Dictionary<string, string> discovered, params string[] parts)
    {
        return discovered.Keys.FirstOrDefault(key =>
            parts.All(part => key.Contains(part, StringComparison.OrdinalIgnoreCase)));
    }
}

internal sealed class IndividualProgressionKeyNames
{
    public string StartingProgression { get; set; } = "IndividualProgression.StartingProgression";

    public string ProgressionLimit { get; set; } = "IndividualProgression.ProgressionLimit";

    public string TbcRacesUnlockProgression { get; set; } = "IndividualProgression.TbcRacesUnlockProgression";

    public string TbcRacesStartingProgression { get; set; } = "IndividualProgression.TbcRacesStartingProgression";

    public ServerWideProgressionKeyMappingDto ToDto() => new()
    {
        StartingProgression = StartingProgression,
        ProgressionLimit = ProgressionLimit,
        TbcRacesUnlockProgression = TbcRacesUnlockProgression,
        TbcRacesStartingProgression = TbcRacesStartingProgression,
    };

    public static IndividualProgressionKeyNames FromDto(ServerWideProgressionKeyMappingDto dto) => new()
    {
        StartingProgression = dto.StartingProgression,
        ProgressionLimit = dto.ProgressionLimit,
        TbcRacesUnlockProgression = dto.TbcRacesUnlockProgression,
        TbcRacesStartingProgression = dto.TbcRacesStartingProgression,
    };
}
