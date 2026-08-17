using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Services.Migrations;

namespace AzerothPlatform.Infrastructure.Services.IndividualProgression;

/// <summary>
/// Reads effective config overrides from a patch's <c>config/*.json</c> files.
/// </summary>
internal static class PatchConfigOverrideReader
{
    public static List<PatchConfigOverrideDto> ReadOverrides(string stackRoot, string patchKey)
    {
        var results = new List<PatchConfigOverrideDto>();
        var configDir = MigrationLayout.ConfigDir(stackRoot, patchKey);
        if (!Directory.Exists(configDir))
        {
            return results;
        }

        foreach (var jsonFile in Directory.EnumerateFiles(configDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            var jsonFileName = Path.GetFileName(jsonFile);
            var baseName = Path.GetFileNameWithoutExtension(jsonFile);
            var sourceJson = $"config/{jsonFileName}";
            var relativeConf = PatchServerConfigResolver.ResolveRelativeConfPath(stackRoot, baseName);
            var targetConf = relativeConf ?? $"{baseName}.conf";

            if (PatchConfigJson.TryLoadOverrides(File.ReadAllText(jsonFile), out var overrides, out _)
                != ConfigOverrideLoadOutcome.Loaded
                || overrides is null)
            {
                continue;
            }

            foreach (var (key, value) in overrides)
            {
                results.Add(new PatchConfigOverrideDto
                {
                    SourceJson = sourceJson,
                    TargetConf = targetConf,
                    Key = key,
                    Value = value,
                });
            }
        }

        return results
            .OrderBy(entry => entry.TargetConf, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<List<PatchConfigOverrideDto>> EnrichWithCurrentValuesAsync(
        string stackId,
        IReadOnlyList<PatchConfigOverrideDto> overrides,
        IServerConfigService serverConfig,
        CancellationToken cancellationToken)
    {
        if (overrides.Count == 0)
        {
            return [];
        }

        var confCache = new Dictionary<string, (bool Found, string Content)>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PatchConfigOverrideDto>(overrides.Count);

        foreach (var entry in overrides)
        {
            if (!confCache.TryGetValue(entry.TargetConf, out var conf))
            {
                try
                {
                    var content = (await serverConfig.ReadAsync(stackId, entry.TargetConf, cancellationToken)).Content;
                    conf = (true, content);
                }
                catch (FileNotFoundException)
                {
                    conf = (false, string.Empty);
                }

                confCache[entry.TargetConf] = conf;
            }

            var enriched = new PatchConfigOverrideDto
            {
                SourceJson = entry.SourceJson,
                TargetConf = entry.TargetConf,
                Key = entry.Key,
                Value = entry.Value,
                ConfFound = conf.Found,
            };

            if (conf.Found && ServerConfigValueEditor.TryGetValue(conf.Content, entry.Key, out var current))
            {
                enriched.KeyFound = true;
                enriched.CurrentValue = current;
            }

            results.Add(enriched);
        }

        return results;
    }
}
