using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Core.Contracts;
using System.Text.RegularExpressions;

namespace AzerothPlatform.Infrastructure.Services.Parsers;

/// <summary>
/// Parses the transmog module configuration file.
/// Extracts transmogrification settings with their descriptions and default values.
/// Groups settings by feature area (Basic, Collection System, Sets, Plus Features) for modal tabs.
/// </summary>
public class TransmogConfigParser : IModuleConfigParser
{
    public string ModuleName => "Transmogrification";

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
        string currentSection = "Basic Settings";

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            // Detect section headers
            if (line.Contains("TRANSMOG PLUS"))
            {
                currentSection = "Plus Features";
            }
            else if (line.Contains("COPPER COST") || line.Contains("COST"))
            {
                currentSection = "Costs & Pricing";
            }
            else if (line.Contains("SETS") && line.StartsWith("#"))
            {
                currentSection = "Transmog Sets";
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

            // Check if this is a setting name
            if (commentText.StartsWith("Transmogrification.") && !commentText.Contains(' '))
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
            else if (commentText.StartsWith("Description:"))
            {
                currentDesc.Add(commentText[12..].Trim());
            }
            else if (commentText.StartsWith("Default:"))
            {
                currentDefault = commentText[8..].Trim();
            }
            else if (commentText.StartsWith("Example:"))
            {
                currentDesc.Add($"Example: {commentText[8..].Trim()}");
            }
            else if (currentKey != null && commentText.Length > 0)
            {
                // Continuation of description
                currentDesc.Add(commentText);
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

            var kvMatch = Regex.Match(line, @"^(Transmogrification\.[^\s=]+)\s*=\s*(.*)$");
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
                        Description: $"Configuration for {key.Replace("Transmogrification.", "")}"
                    ));
                }
            }
        }

        // Organize sections logically
        var orderedSections = OrganizeSections(sections);

        return Task.FromResult(orderedSections.Select(kvp => new ModuleConfigSectionDto
        {
            Name = kvp.Key,
            Options = kvp.Value
        }));
    }

    private string GenerateEnvVarName(string key)
    {
        // Convert "Transmogrification.Enable" to "AC_TRANSMOGRIFICATION_ENABLE"
        var cleaned = key.Replace("Transmogrification.", "");
        var envVar = Regex.Replace(cleaned, @"([a-z])([A-Z])", "$1_$2");
        envVar = envVar.Replace(".", "_");
        return $"AC_TRANSMOGRIFICATION_{envVar.ToUpperInvariant()}";
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

    private Dictionary<string, List<ModuleConfigOption>> OrganizeSections(
        Dictionary<string, List<ModuleConfigOption>> sections)
    {
        var organized = new Dictionary<string, List<ModuleConfigOption>>();

        // Separate into logical feature groups
        var basicSettings = new List<ModuleConfigOption>();
        var collectionSettings = new List<ModuleConfigOption>();
        var setSettings = new List<ModuleConfigOption>();
        var plusSettings = new List<ModuleConfigOption>();
        var costSettings = new List<ModuleConfigOption>();
        var otherSettings = new List<ModuleConfigOption>();

        foreach (var section in sections)
        {
            foreach (var option in section.Value)
            {
                // Categorize by key patterns
                if (option.Key.Contains("Plus") || option.Key.Contains("Membership") || 
                    option.Key.Contains("PetSpell"))
                {
                    plusSettings.Add(option);
                }
                else if (option.Key.Contains("Set"))
                {
                    setSettings.Add(option);
                }
                else if (option.Key.Contains("Collection") || option.Key.Contains("RetroActive") || 
                         option.Key.Contains("TrackUnusable"))
                {
                    collectionSettings.Add(option);
                }
                else if (option.Key.Contains("Cost") || option.Key.Contains("Price"))
                {
                    costSettings.Add(option);
                }
                else if (option.Key is "Transmogrification.Enable" || 
                         option.Key.Contains("AllowHidden") ||
                         option.Key.Contains("Portable") ||
                         option.Key.Contains("Vendor") ||
                         option.Key.Contains("Info") ||
                         option.Key.Contains("Sort"))
                {
                    basicSettings.Add(option);
                }
                else
                {
                    otherSettings.Add(option);
                }
            }
        }

        // Add sections in logical order (only if they have content)
        if (basicSettings.Count > 0)
            organized["Basic Settings"] = basicSettings;

        if (collectionSettings.Count > 0)
            organized["Collection System"] = collectionSettings;

        if (setSettings.Count > 0)
            organized["Transmog Sets"] = setSettings;

        if (costSettings.Count > 0)
            organized["Costs & Pricing"] = costSettings;

        if (plusSettings.Count > 0)
            organized["Plus Features"] = plusSettings;

        if (otherSettings.Count > 0)
            organized["Other"] = otherSettings;

        return organized;
    }
}
