using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Core.Contracts;
using System.Text.RegularExpressions;

namespace AzerothPlatform.Infrastructure.Services.Parsers;

/// <summary>
/// Parses the AH Bot Plus module configuration file.
/// Extracts auction house bot settings with their descriptions and default values.
/// Groups settings by feature area (General, Pricing, Filters, Buyer, Seller, etc.) for modal tabs.
/// </summary>
public class AhBotConfigParser : IModuleConfigParser
{
    public string ModuleName => "Auction House Bot";

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

            // Detect section headers (simplified detection)
            if (line.Contains("PRICING") && !currentSection.Contains("Pricing"))
            {
                currentSection = "Pricing";
            }
            else if (line.Contains("FILTERS"))
            {
                currentSection = "Filters";
            }
            else if (line.Contains("BUYER") && !line.Contains("SELLER"))
            {
                currentSection = "Buyer";
            }
            else if (line.Contains("SELLER") && !line.Contains("BUYER"))
            {
                currentSection = "Seller";
            }
            else if (line.Contains("ALLIANCE") || line.Contains("HORDE"))
            {
                currentSection = "Faction Settings";
            }

            if (!line.StartsWith('#'))
                continue;

            string commentText = line.TrimStart('#').Trim();

            // Skip separator lines
            if (string.IsNullOrWhiteSpace(commentText) ||
                commentText.All(c => c is '#' or '=' or '-' or '*' or ' '))
            {
                continue;
            }

            // Check if this is a setting name (starts with AuctionHouseBot.)
            if (commentText.StartsWith("AuctionHouseBot.") && !commentText.Contains(' ') && !commentText.Contains('*'))
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
                else if (trimmed.StartsWith("Example:") || trimmed.StartsWith("Examples:"))
                {
                    // Skip examples to keep descriptions concise
                    continue;
                }
                else if (!string.IsNullOrWhiteSpace(trimmed) && 
                         !trimmed.StartsWith("Note:") &&
                         !trimmed.StartsWith("Important:") &&
                         !trimmed.StartsWith("Available") &&
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

            // Match AuctionHouseBot.* = value
            var kvMatch = Regex.Match(line, @"^(AuctionHouseBot\.[^\s=]+)\s*=\s*(.*)$");
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
                        Type: InferType(key, doc.DefaultValue),
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
                        Type: InferType(key, value),
                        Description: $"Configuration for {key.Replace("AuctionHouseBot.", "")}"
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
        // Convert "AuctionHouseBot.Buyer.Enabled" to "AC_AUCTION_HOUSE_BOT_BUYER_ENABLED"
        var cleaned = key.Replace("AuctionHouseBot.", "");
        var envVar = Regex.Replace(cleaned, @"([a-z])([A-Z])", "$1_$2");
        envVar = envVar.Replace(".", "_");
        return $"AC_AUCTION_HOUSE_BOT_{envVar.ToUpperInvariant()}";
    }

    private ConfigOptionType InferType(string key, string value)
    {
        // GUID/ID list fields are comma-separated values
        var suffix = key.Contains('.') ? key[(key.LastIndexOf('.') + 1)..] : key;
        if (suffix.EndsWith("GUIDs", StringComparison.OrdinalIgnoreCase) ||
            suffix.EndsWith("Ids", StringComparison.OrdinalIgnoreCase))
        {
            return ConfigOptionType.StringList;
        }

        // Boolean detection
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || 
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
            "Pricing",
            "Buyer",
            "Seller",
            "Filters",
            "Faction Settings",
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
