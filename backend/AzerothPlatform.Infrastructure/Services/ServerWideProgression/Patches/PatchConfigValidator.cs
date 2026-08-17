using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Services.Migrations;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Validates patch <c>config/*.json</c> overrides against configs available on the running stack.
/// </summary>
internal static class PatchConfigValidator
{
    public static async Task ValidateAsync(
        string stackId,
        string stackRoot,
        IServerConfigService serverConfig,
        ICollection<string> errors,
        ICollection<ServerWideProgressionKeyCheckDto> keyChecks,
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
            var configDir = MigrationLayout.ConfigDir(stackRoot, patchKey);
            if (!Directory.Exists(configDir))
            {
                continue;
            }

            foreach (var jsonFile in Directory.EnumerateFiles(configDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                var jsonFileName = Path.GetFileName(jsonFile);
                if (string.Equals(jsonFileName, PatchLauncherConfig.ConfigFileName, StringComparison.OrdinalIgnoreCase))
                {
                    await ValidateLauncherConfigAsync(patchKey, jsonFile, errors, cancellationToken);
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(jsonFile);
                var configSource = $"config/{jsonFileName}";
                var relativeConf = PatchServerConfigResolver.ResolveRelativeConfPath(stackRoot, baseName);

                Dictionary<string, string>? overrides;
                var json = await File.ReadAllTextAsync(jsonFile, cancellationToken);
                var loadOutcome = PatchConfigJson.TryLoadOverrides(json, out overrides, out var parseError);
                if (loadOutcome == ConfigOverrideLoadOutcome.Failed)
                {
                    errors.Add($"{patchKey}: failed to parse {configSource}: {parseError}");
                    continue;
                }

                if (loadOutcome == ConfigOverrideLoadOutcome.Skipped)
                {
                    continue;
                }

                if (relativeConf is null)
                {
                    errors.Add(
                        $"{patchKey}: server config not found for {configSource} (expected {baseName}.conf under the stack etc directory). Rebuild or start the stack once so configs are seeded.");
                    keyChecks.Add(new ServerWideProgressionKeyCheckDto
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
                    keyChecks.Add(new ServerWideProgressionKeyCheckDto
                    {
                        PatchKey = patchKey,
                        ConfigSource = configSource,
                        ConfigPath = relativeConf,
                        Error = "Server config file not found.",
                    });
                    continue;
                }

                foreach (var key in overrides!.Keys)
                {
                    var check = new ServerWideProgressionKeyCheckDto
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

    private static async Task ValidateLauncherConfigAsync(
        string patchKey,
        string jsonFilePath,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var configSource = $"config/{PatchLauncherConfig.ConfigFileName}";
        var json = await File.ReadAllTextAsync(jsonFilePath, cancellationToken);
        if (!PatchLauncherConfig.TryParseTheme(json, out _, out var parseError))
        {
            errors.Add($"{patchKey}: failed to parse {configSource}: {parseError}");
        }
    }
}
