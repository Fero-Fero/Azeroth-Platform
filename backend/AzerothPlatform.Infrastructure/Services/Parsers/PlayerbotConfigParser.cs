using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Core.Contracts;
using System.Text.RegularExpressions;

namespace AzerothPlatform.Infrastructure.Services.Parsers;

/// <summary>
/// Parses the playerbots module configuration file.
/// Extracts settings from the PLAYERBOTS SETTINGS section with their descriptions and default values.
/// Groups settings by subsection (GENERAL, SUMMON OPTIONS, MOUNT, GEAR, etc.) for modal tabs.
/// </summary>
public class PlayerbotConfigParser : IModuleConfigParser
{
    public string ModuleName => "Playerbots";

    public Task<IEnumerable<ModuleConfigSectionDto>> ParseAsync(
        string confFileContent,
        CancellationToken cancellationToken = default)
    {
        var sections = new Dictionary<string, List<ModuleConfigOption>>();
        var lines = confFileContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // Find the start of PLAYERBOTS SETTINGS section
        int startIdx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("# PLAYERBOTS SETTINGS") && !lines[i].Contains("RANDOMBOT-SPECIFIC"))
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx == -1)
            return Task.FromResult(Enumerable.Empty<ModuleConfigSectionDto>());

        // Find the end of PLAYERBOTS SETTINGS (next major section like RANDOMBOT-SPECIFIC, PREMADE SPECS, etc.)
        int endIdx = lines.Length;
        for (int i = startIdx + 1; i < lines.Length; i++)
        {
            if ((lines[i].Contains("RANDOMBOT-SPECIFIC") || 
                 lines[i].Contains("PREMADE SPECS") ||
                 lines[i].Contains("SYSTEM SETTINGS")) && 
                lines[i].Contains("#"))
            {
                endIdx = i;
                break;
            }
        }

        // Parse settings within PLAYERBOTS SETTINGS section
        string currentSection = "General";
        var commentBuffer = new List<string>();

        for (int i = startIdx; i < endIdx; i++)
        {
            string line = lines[i];

            // Detect subsection headers (e.g., "# SUMMON OPTIONS")
            if (line.StartsWith("####") && line.Contains("#"))
            {
                // Extract section name from header
                var match = Regex.Match(line, @"#\s*([A-Z\s]+?)\s*#");
                if (match.Success)
                {
                    string sectionName = match.Groups[1].Value.Trim();
                    // Clean up the section name
                    if (!string.IsNullOrWhiteSpace(sectionName) && 
                        sectionName != "PLAYERBOTS SETTINGS" &&
                        !sectionName.Contains("##"))
                    {
                        currentSection = sectionName;
                        if (!sections.ContainsKey(currentSection))
                            sections[currentSection] = new List<ModuleConfigOption>();
                        commentBuffer.Clear();
                        continue;
                    }
                }
            }

            // Collect comment lines (descriptions)
            if (line.StartsWith("#") && !line.StartsWith("####"))
            {
                string comment = line[1..].Trim();
                if (!string.IsNullOrWhiteSpace(comment))
                    commentBuffer.Add(comment);
                continue;
            }

            // Parse configuration settings (e.g., "AiPlayerbot.MaxAddedBots = 40")
            if (line.StartsWith("AiPlayerbot.") && line.Contains('='))
            {
                var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                    continue;

                string configKey = parts[0].Trim();
                string defaultValue = parts[1].Trim();

                // Ensure section exists
                if (!sections.ContainsKey(currentSection))
                    sections[currentSection] = new List<ModuleConfigOption>();

                // Build description from comments
                string description = string.Join(" ", commentBuffer);
                if (string.IsNullOrWhiteSpace(description))
                    description = "No description available";

                // Convert config key to environment variable name
                string envVarName = ConfigKeyToEnvVar(configKey);

                // Infer type
                ConfigOptionType type = InferType(defaultValue, description);

                var option = new ModuleConfigOption(
                    configKey,
                    envVarName,
                    defaultValue,
                    type,
                    description
                );

                sections[currentSection].Add(option);
                commentBuffer.Clear();
            }
            else if (!line.StartsWith("#") && !string.IsNullOrWhiteSpace(line))
            {
                // Non-comment, non-setting line clears comment buffer
                commentBuffer.Clear();
            }
        }

        // Convert to return format with General section first
        var result = sections
            .Select(kvp => new ModuleConfigSectionDto
            {
                Name = kvp.Key,
                Options = kvp.Value
            })
            .OrderBy(s => s.Name == "General" ? "0" : s.Name) // General first, then alphabetically
            .ToList();

        return Task.FromResult(result.AsEnumerable());
    }

    /// <summary>
    /// Converts a config key (e.g., "AiPlayerbot.MaxAddedBots") to an environment variable name.
    /// Result: AC_AI_PLAYERBOT_MAX_ADDED_BOTS
    /// </summary>
    private string ConfigKeyToEnvVar(string configKey)
    {
        // Split on dots first (e.g., "AiPlayerbot.MaxAddedBots" -> ["AiPlayerbot", "MaxAddedBots"])
        var parts = configKey.Split('.');

        var envParts = new List<string>();
        foreach (var part in parts)
        {
            // Split on capital letters, but keep consecutive capitals together
            var words = SplitOnCapitals(part);
            envParts.AddRange(words);
        }

        // Join with underscores and uppercase
        return "AC_" + string.Join("_", envParts).ToUpper();
    }

    /// <summary>
    /// Infers the data type of a configuration value based on its default value and description.
    /// </summary>
    private ConfigOptionType InferType(string defaultValue, string description)
    {
        // Remove quotes if present
        string cleanValue = defaultValue.Trim('"', '\'');

        // Check for enum patterns (e.g., "0 = OFF, 1 = ON")
        if ((cleanValue == "0" || cleanValue == "1") && 
            description.Contains(" = ", StringComparison.OrdinalIgnoreCase))
        {
            return ConfigOptionType.Enum;
        }

        // Check if boolean
        if (cleanValue == "0" || cleanValue == "1" ||
            description.Contains("Enable", StringComparison.OrdinalIgnoreCase) || 
            description.Contains("Disable", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("enabled", StringComparison.OrdinalIgnoreCase) || 
            description.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("on/off", StringComparison.OrdinalIgnoreCase) || 
            description.Contains("true/false", StringComparison.OrdinalIgnoreCase))
        {
            return ConfigOptionType.Boolean;
        }

        // Check if decimal/float
        if (cleanValue.Contains('.') && decimal.TryParse(cleanValue, out _))
            return ConfigOptionType.Number;

        // Check if integer
        if (int.TryParse(cleanValue, out _))
            return ConfigOptionType.Number;

        // Default to string
        return ConfigOptionType.String;
    }

    /// <summary>
    /// Splits a camelCase string on capital letters, preserving consecutive capitals.
    /// Examples:
    /// - "MaxAddedBots" -> ["Max", "Added", "Bots"]
    /// - "RPGStrategy" -> ["RPG", "Strategy"]
    /// - "DebugWhisper" -> ["Debug", "Whisper"]
    /// </summary>
    private List<string> SplitOnCapitals(string input)
    {
        if (string.IsNullOrEmpty(input))
            return new List<string>();

        var result = new List<string>();
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (char.IsUpper(c) && i > 0)
            {
                bool isPreviousCapital = char.IsUpper(input[i - 1]);
                bool isNextLowercase = i < input.Length - 1 && char.IsLower(input[i + 1]);

                // If previous is capital and next is lowercase, split before this char
                if (isPreviousCapital && isNextLowercase && current.Length > 1)
                {
                    // Move the last character of current to new word
                    string currentStr = current.ToString();
                    current.Clear();
                    current.Append(currentStr[..^1]);
                    if (current.Length > 0)
                        result.Add(current.ToString());
                    current.Clear();
                    current.Append(currentStr[^1]);
                    current.Append(c);
                }
                else if (!isPreviousCapital)
                {
                    // Normal case: lowercase followed by uppercase
                    if (current.Length > 0)
                        result.Add(current.ToString());
                    current.Clear();
                    current.Append(c);
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }
}
