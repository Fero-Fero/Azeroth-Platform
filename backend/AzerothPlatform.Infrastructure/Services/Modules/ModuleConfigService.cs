using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

public sealed partial class ModuleConfigService : IModuleConfigService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModuleConfigService> _logger;
    private readonly IEnumerable<IModuleConfigParser> _moduleParsers;
    private readonly ConcurrentDictionary<string, CachedConfigSchema> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private static readonly Dictionary<string, ModuleConfFile> ModuleConfFiles = new()
    {
        ["mod-playerbots"] = new("mod-playerbots", "mod-playerbots", "master", "conf/playerbots.conf.dist"),
        ["mod-autobalance"] = new("azerothcore", "mod-autobalance", "master", "conf/AutoBalance.conf.dist"),
        ["mod-transmog"] = new("azerothcore", "mod-transmog", "master", "conf/transmog.conf.dist"),
        ["mod-ah-bot"] = new("NathanHandley", "mod-ah-bot-plus", "master", "conf/mod_ahbot.conf.dist")
    };

    public ModuleConfigService(
        IHttpClientFactory httpClientFactory,
        ILogger<ModuleConfigService> logger,
        IEnumerable<IModuleConfigParser>? moduleParsers = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _moduleParsers = moduleParsers ?? Enumerable.Empty<IModuleConfigParser>();
    }

    public async Task<ModuleConfigSchema> GetConfigSchemaAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        if (!ModuleConfFiles.TryGetValue(moduleId, out var confFile))
        {
            throw new ArgumentException($"Unknown module ID: {moduleId}", nameof(moduleId));
        }

        if (_cache.TryGetValue(moduleId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            _logger.LogDebug("Returning cached config schema for {ModuleId}", moduleId);
            return cached.Schema;
        }

        _logger.LogInformation("Fetching and parsing config file for {ModuleId} from GitHub", moduleId);
        var rawConfContent = await FetchConfFileAsync(confFile, cancellationToken);
        
        // Try to use module-specific parser first
        var moduleName = GetModuleName(moduleId);
        var parser = _moduleParsers.FirstOrDefault(p => p.ModuleName == moduleName);
        
        ModuleConfigSchema schema;
        if (parser != null)
        {
            _logger.LogInformation("Using module-specific parser for {ModuleId}", moduleId);
            schema = await ParseWithModuleParserAsync(moduleId, moduleName, rawConfContent, parser, cancellationToken);
        }
        else
        {
            _logger.LogWarning("No module-specific parser found for {ModuleId}, using generic parser", moduleId);
            schema = ParseConfFile(moduleId, moduleName, rawConfContent);
        }

        _cache[moduleId] = new CachedConfigSchema(schema, DateTime.UtcNow.Add(CacheDuration));

        return schema;
    }

    public Task RefreshCacheAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Clearing module config cache");
        _cache.Clear();
        return Task.CompletedTask;
    }

    private async Task<string> FetchConfFileAsync(ModuleConfFile confFile, CancellationToken cancellationToken)
    {
        var url = $"https://raw.githubusercontent.com/{confFile.Owner}/{confFile.Repo}/{confFile.Branch}/{confFile.Path}";

        using var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to fetch conf file from {url}: {response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static ModuleConfigSchema ParseConfFile(string moduleId, string moduleName, string rawContent)
    {
        var lines = rawContent.Split('\n', StringSplitOptions.TrimEntries);
        var options = new List<ModuleConfigOption>();
        
        // Build a map of setting names to their descriptions
        var descriptionMap = new Dictionary<string, List<string>>();
        var currentSettingNames = new List<string>(); // Support multiple settings per description block
        var currentDescription = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith('#'))
            {
                var commentText = line.TrimStart('#').Trim();
                
                // Skip separator lines
                if (string.IsNullOrWhiteSpace(commentText) || 
                    commentText.All(c => c is '#' or '=' or '-' or '*' or ' '))
                {
                    continue;
                }

                // Check if this line contains a setting name (looks like a config key)
                // E.g., "PlayerbotAI.DebugWhisper" or "PlayerbotAI.FollowDistanceMin"
                if (commentText.Contains('.') && !commentText.Contains(' '))
                {
                    // This is a setting name - add to current list
                    currentSettingNames.Add(commentText);
                }
                else if (currentSettingNames.Count > 0)
                {
                    // This is part of the description for the current setting(s)
                    currentDescription.Add(commentText);
                }
            }
            else
            {
                // Non-comment line - save any pending descriptions
                if (currentSettingNames.Count > 0 && currentDescription.Count > 0)
                {
                    // Apply the same description to ALL settings in the current block
                    var descCopy = new List<string>(currentDescription);
                    foreach (var settingName in currentSettingNames)
                    {
                        descriptionMap[settingName] = new List<string>(descCopy);
                    }
                }
                
                // Clear for next block
                currentSettingNames.Clear();
                currentDescription.Clear();
            }
        }

        // Now parse actual key=value lines and match with descriptions
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = KeyValueRegex().Match(line);
            
            if (match.Success)
            {
                var key = match.Groups[1].Value.Trim();
                var value = match.Groups[2].Value.Trim();

                // Skip empty keys or section headers
                if (string.IsNullOrWhiteSpace(key) || key.StartsWith('['))
                {
                    continue;
                }

                // Find description for this key
                var description = string.Empty;
                if (descriptionMap.TryGetValue(key, out var descLines))
                {
                    description = CleanDescription(descLines);
                }

                var envVarName = ConvertToEnvVarName(key);
                var (type, enumOptions) = InferType(value, description);

                options.Add(new ModuleConfigOption(
                    Key: key,
                    EnvVarName: envVarName,
                    DefaultValue: value,
                    Type: type,
                    Description: description,
                    EnumOptions: enumOptions
                ));
            }
        }

        return new ModuleConfigSchema(moduleId, moduleName, options.ToArray());
    }

    private async Task<ModuleConfigSchema> ParseWithModuleParserAsync(
        string moduleId,
        string moduleName,
        string confContent,
        IModuleConfigParser parser,
        CancellationToken cancellationToken)
    {
        var sections = await parser.ParseAsync(confContent, cancellationToken);
        
        // Flatten all options from all sections into a single array
        var allOptions = sections
            .SelectMany(section => section.Options)
            .ToArray();

        _logger.LogInformation("Parsed {Count} configuration options for {ModuleId}", allOptions.Length, moduleId);

        return new ModuleConfigSchema(moduleId, moduleName, allOptions);
    }

    private static string CleanDescription(List<string> descriptionLines)
    {
        if (descriptionLines.Count == 0)
        {
            return string.Empty;
        }

        // Filter out "Default:" lines since we show defaults in the field
        var filtered = descriptionLines
            .Where(line => !line.StartsWith("Default:", StringComparison.OrdinalIgnoreCase))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (filtered.Count == 0)
        {
            return string.Empty;
        }

        // Join lines and clean up
        var joined = string.Join(" ", filtered)
            .Replace("  ", " ") // Remove double spaces
            .Replace(" .", ".") // Fix spacing before periods
            .Replace(" ,", ",") // Fix spacing before commas
            .Trim();

        return joined;
    }

    private static string ConvertToEnvVarName(string confKey)
    {
        // Convert "AutoBalance.Enable.Global" to "AC_AUTO_BALANCE_ENABLE_GLOBAL"
        // 1. Split on dots
        // 2. For each segment, split on capital letters
        // 3. Join all parts with underscores
        // 4. Uppercase everything
        // 5. Prefix with AC_

        var segments = confKey.Split('.');
        var words = new List<string>();

        foreach (var segment in segments)
        {
            // Split on capital letters: "AutoBalance" -> ["Auto", "Balance"]
            var segmentWords = SplitOnCapitals(segment);
            words.AddRange(segmentWords);
        }

        var envVar = string.Join("_", words).ToUpperInvariant();
        return $"AC_{envVar}";
    }

    private static List<string> SplitOnCapitals(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return [];
        }

        var words = new List<string>();
        var currentWord = new List<char>();

        for (int i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            var isUpper = char.IsUpper(ch);
            var hasNext = i + 1 < input.Length;
            var hasPrev = i > 0;
            
            // Check if we should start a new word
            bool shouldSplit = false;
            
            if (isUpper && currentWord.Count > 0)
            {
                // We have an uppercase letter and we've started a word
                var prevChar = input[i - 1];
                var prevIsLower = char.IsLower(prevChar);
                
                // Split if previous char was lowercase
                // E.g., "PlayerbotAI" -> split between 't' and 'A'
                // "AI" stays together (both uppercase)
                if (prevIsLower)
                {
                    shouldSplit = true;
                }
                // Also split if this is uppercase, next is lowercase, and previous was uppercase
                // E.g., "AIDebug" -> ["AI", "Debug"] (split between I and D)
                else if (hasNext && char.IsLower(input[i + 1]) && !prevIsLower)
                {
                    // This handles "RPGMod" -> ["RPG", "Mod"]
                    // We're at 'M', prev was 'G' (upper), next is 'o' (lower)
                    // But we actually want to keep 'M' with the next word
                    // So split BEFORE adding this char
                    shouldSplit = true;
                }
            }
            
            if (shouldSplit)
            {
                words.Add(new string(currentWord.ToArray()));
                currentWord.Clear();
            }

            currentWord.Add(ch);
        }

        if (currentWord.Count > 0)
        {
            words.Add(new string(currentWord.ToArray()));
        }

        return words;
    }

    private static (ConfigOptionType Type, string[]? EnumOptions) InferType(string value, string description)
    {
        // Check for enum patterns first (most specific)
        if (value is "0" or "1" && description.Contains("0 = ", StringComparison.OrdinalIgnoreCase))
        {
            // Extract enum options from description like "1 = ON, 0 = OFF"
            var enumMatch = EnumPatternRegex().Match(description);
            if (enumMatch.Success)
            {
                var options = enumMatch.Groups[1].Value
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(opt => opt.Trim())
                    .ToArray();

                return (ConfigOptionType.Enum, options);
            }
        }
        
        // Check for boolean - 0 or 1 values (with or without description)
        if (value is "0" or "1")
        {
            // Look for boolean indicators in description
            var lowerDesc = description.ToLowerInvariant();
            if (lowerDesc.Contains("enable") || lowerDesc.Contains("disable") || 
                lowerDesc.Contains("on") || lowerDesc.Contains("off") ||
                lowerDesc.Contains("true") || lowerDesc.Contains("false") ||
                string.IsNullOrEmpty(description)) // Default 0/1 to boolean if no description
            {
                return (ConfigOptionType.Boolean, null);
            }
        }

        // Check for numeric
        if (int.TryParse(value, out _) || double.TryParse(value, out _))
        {
            return (ConfigOptionType.Number, null);
        }

        // Default to string
        return (ConfigOptionType.String, null);
    }

    private static string GetModuleName(string moduleId) => moduleId switch
    {
        "mod-playerbots" => "Playerbots",
        "mod-autobalance" => "Auto Balance",
        "mod-transmog" => "Transmogrification",
        "mod-ah-bot" => "Auction House Bot",
        _ => moduleId
    };

    [GeneratedRegex(@"^([^=]+)=(.*)$")]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"\(([^)]+)\)")]
    private static partial Regex EnumPatternRegex();

    private sealed record ModuleConfFile(string Owner, string Repo, string Branch, string Path);
    private sealed record CachedConfigSchema(ModuleConfigSchema Schema, DateTime ExpiresAt);
}
