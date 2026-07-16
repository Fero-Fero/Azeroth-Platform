using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Services.Migrations;

namespace AzerothPlatform.Infrastructure.Services.IndividualProgression;

/// <summary>
/// Validates patch <c>config/*.json</c> overrides against configs available on the running stack.
/// </summary>
internal static class PatchConfigValidator
{
    private const string ProgressionMetadataFileName = "progression.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task ValidateAsync(
        string stackId,
        string stackRoot,
        IServerConfigService serverConfig,
        ICollection<string> errors,
        ICollection<IndividualProgressionKeyCheckDto> keyChecks,
        CancellationToken cancellationToken)
    {
        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        if (!Directory.Exists(migrationsRoot))
        {
            return;
        }

        foreach (var patchDir in Directory.EnumerateDirectories(migrationsRoot))
        {
            var patchKey = Path.GetFileName(patchDir);
            if (!File.Exists(Path.Combine(patchDir, ProgressionMetadataFileName)))
            {
                continue;
            }

            var configDir = MigrationLayout.ConfigDir(stackRoot, patchKey);
            if (!Directory.Exists(configDir))
            {
                continue;
            }

            foreach (var jsonFile in Directory.EnumerateFiles(configDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                var jsonFileName = Path.GetFileName(jsonFile);
                var baseName = Path.GetFileNameWithoutExtension(jsonFile);
                var configSource = $"config/{jsonFileName}";
                var relativeConf = PatchServerConfigResolver.ResolveRelativeConfPath(stackRoot, baseName);

                Dictionary<string, string>? overrides;
                try
                {
                    var json = await File.ReadAllTextAsync(jsonFile, cancellationToken);
                    overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
                }
                catch (Exception ex)
                {
                    errors.Add($"{patchKey}: failed to parse {configSource}: {ex.Message}");
                    continue;
                }

                if (overrides is null || overrides.Count == 0)
                {
                    continue;
                }

                if (relativeConf is null)
                {
                    errors.Add(
                        $"{patchKey}: server config not found for {configSource} (expected {baseName}.conf under the stack etc directory). Rebuild or start the stack once so configs are seeded.");
                    keyChecks.Add(new IndividualProgressionKeyCheckDto
                    {
                        PatchKey = patchKey,
                        ConfigSource = configSource,
                        ConfigPath = $"{baseName}.conf",
                        Error = "Server config file not found.",
                    });
                    continue;
                }

                string content;
                try
                {
                    content = (await serverConfig.ReadAsync(stackId, relativeConf, cancellationToken)).Content;
                }
                catch (FileNotFoundException)
                {
                    errors.Add($"{patchKey}: server config {relativeConf} for {configSource} is not available.");
                    keyChecks.Add(new IndividualProgressionKeyCheckDto
                    {
                        PatchKey = patchKey,
                        ConfigSource = configSource,
                        ConfigPath = relativeConf,
                        Error = "Server config file not found.",
                    });
                    continue;
                }

                foreach (var key in overrides.Keys)
                {
                    var check = new IndividualProgressionKeyCheckDto
                    {
                        PatchKey = patchKey,
                        ConfigSource = configSource,
                        Key = key,
                        ConfigPath = relativeConf,
                    };

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        check.Error = "Server config is empty.";
                        errors.Add($"{patchKey} {configSource}: {relativeConf} is empty — key '{key}' unavailable.");
                    }
                    else if (!ServerConfigValueEditor.TryGetValue(content, key, out var value))
                    {
                        check.Error = "Key not found on server.";
                        errors.Add($"{patchKey} {configSource}: key '{key}' not found in {relativeConf}.");
                    }
                    else
                    {
                        check.Exists = true;
                        check.CanRead = true;
                        check.Value = value;
                    }

                    keyChecks.Add(check);
                }
            }
        }
    }
}
