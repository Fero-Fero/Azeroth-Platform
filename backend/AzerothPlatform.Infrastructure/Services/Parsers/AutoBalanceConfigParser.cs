using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Core.Contracts;
using System.Text.RegularExpressions;

namespace AzerothPlatform.Infrastructure.Services.Parsers;

/// <summary>
/// Parses the AutoBalance module configuration file.
/// Extracts AutoBalance settings with their descriptions and default values.
/// Groups settings by feature area (Enable/Disable, Scaling, Rewards, etc.) for modal tabs.
/// </summary>
public class AutoBalanceConfigParser : IModuleConfigParser
{
    public string ModuleName => "Auto Balance";

    private record SettingDocumentation(string Description, string DefaultValue, string Section);

    public Task<IEnumerable<ModuleConfigSectionDto>> ParseAsync(
        string confFileContent,
        CancellationToken cancellationToken = default)
    {
        var lines = confFileContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // First pass: Extract all documentation for each setting key
        var settingDocs = new Dictionary<string, SettingDocumentation>();
        string? currentKey = null;
        var currentDesc = new List<string>();
        string? currentDefault = null;
        string currentSection = "General";

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            // Detect section headers
            if (line.Contains("Enable / Disable"))
            {
                currentSection = "Enable/Disable";
            }
            else if (line.Contains("Minimum Players"))
            {
                currentSection = "Minimum Players";
            }
            else if (line.Contains("Inflection Point"))
            {
                currentSection = "Scaling - Inflection Point";
            }
            else if (line.Contains("Stat Modifiers") && !line.Contains("Overrides"))
            {
                currentSection = "Scaling - Stat Modifiers";
            }
            else if (line.Contains("Stat Modifier Overrides"))
            {
                currentSection = "Scaling - Overrides";
            }
            else if (line.Contains("Level Scaling"))
            {
                currentSection = "Level Scaling";
            }
            else if (line.Contains("Reward Scaling"))
            {
                currentSection = "Reward Scaling";
            }
            else if (line.Contains("Misc Settings"))
            {
                currentSection = "Miscellaneous";
            }
            else if (line.Contains("Messages"))
            {
                currentSection = "Messages";
            }
            else if (line.Contains("Announcement"))
            {
                currentSection = "Announcements";
            }

            if (!line.StartsWith("#"))
                continue;

            string commentText = line.TrimStart('#').Trim();

            // Skip separator lines
            if (string.IsNullOrWhiteSpace(commentText) ||
                commentText.All(c => c is '#' or '=' or '-' or '*' or ' '))
            {
                continue;
            }

            // Check if this is a setting name (starts with AutoBalance.)
            if (commentText.StartsWith("AutoBalance.") && !commentText.Contains(' ') && !commentText.Contains('*'))
            {
                // Save previous documentation
                if (currentKey != null && currentDefault != null)
                {
                    settingDocs[currentKey] = new SettingDocumentation(
                        string.Join(" ", currentDesc),
                        currentDefault,
                        currentSection
                    );
                }

                // Start new setting
                currentKey = commentText;
                currentDesc.Clear();
                currentDefault = null;
            }
            else if (currentKey != null)
            {
                // Look for description or default value
                var trimmed = commentText.Trim();
                
                if (trimmed.StartsWith("Default:"))
                {
                    currentDefault = trimmed[8..].Trim();
                }
                else if (trimmed.StartsWith("Example:"))
                {
                    currentDesc.Add($"Example: {trimmed[8..].Trim()}");
                }
                else if (!string.IsNullOrWhiteSpace(trimmed) && 
                         !trimmed.StartsWith("Get a list") &&
                         !trimmed.StartsWith("Format:") &&
                         !trimmed.StartsWith("NOTE:") &&
                         trimmed.Length > 5)
                {
                    // Add to description
                    currentDesc.Add(trimmed);
                }
            }
        }

        // Save final documentation
        if (currentKey != null && currentDefault != null)
        {
            settingDocs[currentKey] = new SettingDocumentation(
                string.Join(" ", currentDesc),
                currentDefault,
                currentSection
            );
        }

        // Second pass: Extract actual key-value pairs and match with documentation
        var sections = new Dictionary<string, List<ModuleConfigOption>>();
        
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            // Match AutoBalance.* = value
            var kvMatch = Regex.Match(line, @"^(AutoBalance\.[^\s=]+)\s*=\s*(.*)$");
            if (kvMatch.Success)
            {
                string key = kvMatch.Groups[1].Value.Trim();
                string value = kvMatch.Groups[2].Value.Trim().Trim('"');

                // Try to find documentation for this key
                if (settingDocs.TryGetValue(key, out var doc))
                {
                    if (!sections.ContainsKey(doc.Section))
                        sections[doc.Section] = new List<ModuleConfigOption>();

                    sections[doc.Section].Add(new ModuleConfigOption(
                        Key: key,
                        EnvVarName: GenerateEnvVarName(key),
                        DefaultValue: doc.DefaultValue,
                        Type: InferType(doc.DefaultValue),
                        Description: doc.Description
                    ));
                }
                else
                {
                    // No documentation - add to "Other" section
                    if (!sections.ContainsKey("Other"))
                        sections["Other"] = new List<ModuleConfigOption>();

                    sections["Other"].Add(new ModuleConfigOption(
                        Key: key,
                        EnvVarName: GenerateEnvVarName(key),
                        DefaultValue: value,
                        Type: InferType(value),
                        Description: $"Configuration for {key.Replace("AutoBalance.", "")}"
                    ));
                }
            }
        }

        // Return sections in logical order
        var orderedSections = OrderSections(sections);

        return Task.FromResult(orderedSections.Select(kvp => new ModuleConfigSectionDto
        {
            Name = kvp.Key,
            Options = kvp.Value
        }));
    }

    private string GenerateEnvVarName(string key)
    {
        // Convert "AutoBalance.Enable.Global" to "AC_AUTO_BALANCE_ENABLE_GLOBAL"
        var cleaned = key.Replace("AutoBalance.", "");
        var envVar = Regex.Replace(cleaned, @"([a-z])([A-Z])", "$1_$2");
        envVar = envVar.Replace(".", "_");
        return $"AC_AUTO_BALANCE_{envVar.ToUpperInvariant()}";
    }

    private ConfigOptionType InferType(string value)
    {
        // Boolean detection
        if (value is "0" or "1" || 
            value.Equals("true", StringComparison.OrdinalIgnoreCase) || 
            value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return ConfigOptionType.Boolean;
        }

        // Number detection (including decimals)
        if (double.TryParse(value, out _))
        {
            return ConfigOptionType.Number;
        }

        // Empty string or list format
        if (string.IsNullOrEmpty(value) || value == "\"\"")
        {
            return ConfigOptionType.String;
        }

        return ConfigOptionType.String;
    }

    private Dictionary<string, List<ModuleConfigOption>> OrderSections(
        Dictionary<string, List<ModuleConfigOption>> sections)
    {
        var ordered = new Dictionary<string, List<ModuleConfigOption>>();

        // Define logical order
        var sectionOrder = new[]
        {
            "General",
            "Enable/Disable",
            "Minimum Players",
            "Scaling - Inflection Point",
            "Scaling - Stat Modifiers",
            "Scaling - Overrides",
            "Level Scaling",
            "Reward Scaling",
            "Messages",
            "Announcements",
            "Miscellaneous",
            "Other"
        };

        foreach (var sectionName in sectionOrder)
        {
            if (sections.ContainsKey(sectionName) && sections[sectionName].Count > 0)
            {
                ordered[sectionName] = sections[sectionName];
            }
        }

        // Add any sections not in the predefined order
        foreach (var kvp in sections)
        {
            if (!ordered.ContainsKey(kvp.Key) && kvp.Value.Count > 0)
            {
                ordered[kvp.Key] = kvp.Value;
            }
        }

        return ordered;
    }
}
