using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Patches;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Default <see cref="IConfigMigrationService"/> implementation. Captures the operator's existing
/// server .conf from a stack's <c>etc</c> volume before a build, then after the build produces new
/// images it extracts the new <c>*.conf.dist</c> defaults from those images and writes effective
/// <c>*.conf</c> into the stack's local etc mirror. On the next start, <c>StackService</c> seeds that
/// mirror into the (persistent) <c>etc</c> volume, so the new defaults + preserved values take effect.
/// </summary>
public sealed class ConfigMigrationService : IConfigMigrationService
{
    // AzerothCore runtime images ship config templates under env/ref/etc (dist/etc is the live volume mount).
    private static readonly string[] ImageEtcPaths =
    [
        "/azerothcore/env/ref/etc",
        "/azerothcore/env/dist/etc",
    ];
    private const string BackupDirName = "config-backup";
    private const string TmpDirName = "config-migrate-tmp";

    private readonly string _buildsPath;
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly ILogger<ConfigMigrationService> _logger;

    public ConfigMigrationService(
        IOptions<DockerOptions> dockerOptions,
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine,
        ILogger<ConfigMigrationService> logger)
    {
        var buildsPath = dockerOptions.Value.BuildsPath;
        _buildsPath = Path.IsPathRooted(buildsPath) ? buildsPath : Path.GetFullPath(buildsPath);
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
        _logger = logger;
    }

    public async Task CaptureAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return;
        }

        var backupDir = BackupDir(stackId);
        try
        {
            var volume = DockerComposeOverrideGenerator.EtcVolumeName(stackId);
            if (!await _remoteEngine.VolumeExistsAsync(stack, volume, cancellationToken))
            {
                _logger.LogInformation("No etc volume for stack {StackId}; skipping config capture.", stackId);
                return;
            }

            // Start from a clean backup so a previous migration's snapshot never bleeds in.
            if (Directory.Exists(backupDir))
            {
                Directory.Delete(backupDir, recursive: true);
            }
            Directory.CreateDirectory(backupDir);

            await _remoteEngine.FetchVolumeAsync(stack, volume, backupDir, cancellationToken);
            _logger.LogInformation("Captured server config for stack {StackId} to {Dir}.", stackId, backupDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture server config for stack {StackId}", stackId);
        }
    }

    public async Task ApplyAsync(string stackId, ConfigMigrationMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == ConfigMigrationMode.Skip)
        {
            return;
        }

        var stack = await _dbContext.ManagedStacks.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return;
        }

        var stackRoot = Path.Combine(_buildsPath, stackId);
        var etcLocal = MigrationLayout.EtcDir(stackRoot);
        var backupDir = BackupDir(stackId);
        var tmpRoot = Path.Combine(stackRoot, TmpDirName);

        try
        {
            if (Directory.Exists(tmpRoot))
            {
                Directory.Delete(tmpRoot, recursive: true);
            }

            var worldDist = Path.Combine(tmpRoot, "world");
            var authDist = Path.Combine(tmpRoot, "auth");

            // Stack images are built locally before being shipped, so they are present on the local daemon.
            var worldOk = await ExtractEtcFromImageAsync(WorldserverImage(stackId), worldDist, cancellationToken);
            var authOk = await ExtractEtcFromImageAsync(AuthserverImage(stackId), authDist, cancellationToken);

            if (!worldOk && !authOk)
            {
                _logger.LogWarning("Could not extract new config defaults for stack {StackId}; leaving configs untouched.", stackId);
                return;
            }

            Directory.CreateDirectory(etcLocal);
            var merge = mode == ConfigMigrationMode.Merge;
            var applied = 0;

            // worldserver.conf (+ its dist) from the worldserver image.
            if (worldOk)
            {
                applied += MaterializeConf(
                    distSource: Path.Combine(worldDist, "worldserver.conf.dist"),
                    oldConf: Path.Combine(backupDir, "worldserver.conf"),
                    etcTarget: Path.Combine(etcLocal, "worldserver.conf"),
                    merge);

                // Module configs ship their dist under etc/modules in the worldserver image.
                var modulesDist = Path.Combine(worldDist, "modules");
                if (Directory.Exists(modulesDist))
                {
                    var etcModules = Path.Combine(etcLocal, "modules");
                    foreach (var dist in Directory.EnumerateFiles(modulesDist, "*.conf.dist", SearchOption.TopDirectoryOnly))
                    {
                        var confName = Path.GetFileName(dist)[..^".dist".Length]; // foo.conf.dist -> foo.conf
                        applied += MaterializeConf(
                            distSource: dist,
                            oldConf: Path.Combine(backupDir, "modules", confName),
                            etcTarget: Path.Combine(etcModules, confName),
                            merge);
                    }
                }
            }

            // authserver.conf (+ its dist), preferring the authserver image but falling back to the world image.
            var authDistFile = Path.Combine(authDist, "authserver.conf.dist");
            if (!File.Exists(authDistFile))
            {
                authDistFile = Path.Combine(worldDist, "authserver.conf.dist");
            }
            if (File.Exists(authDistFile))
            {
                applied += MaterializeConf(
                    distSource: authDistFile,
                    oldConf: Path.Combine(backupDir, "authserver.conf"),
                    etcTarget: Path.Combine(etcLocal, "authserver.conf"),
                    merge);
            }

            _logger.LogInformation(
                "Config migration ({Mode}) materialized {Count} config file(s) for stack {StackId}.",
                mode, applied, stackId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply config migration for stack {StackId}", stackId);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tmpRoot))
                {
                    Directory.Delete(tmpRoot, recursive: true);
                }
            }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Writes the refreshed <c>.conf.dist</c> and the effective <c>.conf</c> for a single config file.
    /// In merge mode the new dist is the base and any key also present in the old .conf keeps its old
    /// value; otherwise the new dist defaults are used as-is. Returns 1 when a file was written, else 0.
    /// </summary>
    private int MaterializeConf(string distSource, string oldConf, string etcTarget, bool merge)
    {
        if (!File.Exists(distSource))
        {
            return 0;
        }

        var newDist = File.ReadAllText(distSource);

        Directory.CreateDirectory(Path.GetDirectoryName(etcTarget)!);

        // Refresh the .conf.dist reference so the new defaults (and module option docs) are available.
        File.WriteAllText(etcTarget + ".dist", newDist);

        string effective;
        if (merge && File.Exists(oldConf))
        {
            var oldValues = ParseConfValues(File.ReadAllText(oldConf));
            effective = MergeConf(newDist, oldValues);
        }
        else
        {
            effective = newDist;
        }

        File.WriteAllText(etcTarget, effective);
        return 1;
    }

    private async Task<bool> ExtractEtcFromImageAsync(string image, string destinationDir, CancellationToken cancellationToken)
    {
        foreach (var imagePath in ImageEtcPaths)
        {
            if (Directory.Exists(destinationDir))
            {
                Directory.Delete(destinationDir, recursive: true);
            }

            if (await _remoteEngine.ExtractImageDirAsync(image, imagePath, destinationDir, cancellationToken)
                && Directory.Exists(destinationDir)
                && Directory.EnumerateFiles(destinationDir, "*", SearchOption.AllDirectories).Any())
            {
                return true;
            }
        }

        return false;
    }

    private string BackupDir(string stackId) => Path.Combine(_buildsPath, stackId, BackupDirName);

    // Mirrors BuildService.StackImageTags (kept in sync intentionally).
    private static string WorldserverImage(string stackId) => $"acore/ac-wotlk-worldserver:{stackId}";
    private static string AuthserverImage(string stackId) => $"acore/ac-wotlk-authserver:{stackId}";

    /// <summary>
    /// Rewrites <paramref name="newDistContent"/> so that every <c>Key = value</c> line whose key also
    /// exists in <paramref name="oldValues"/> keeps the old value. Comments, blank lines, section
    /// markers, and keys only present in the new config are preserved unchanged.
    /// </summary>
    private static string MergeConf(string newDistContent, IReadOnlyDictionary<string, string> oldValues)
    {
        var lines = newDistContent.Split('\n');
        var sb = new StringBuilder(newDistContent.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd('\r');

            if (TryParseKeyLine(line, out var key, out var eqIndex) && oldValues.TryGetValue(key, out var oldValue))
            {
                var left = line[..(eqIndex + 1)].TrimEnd(); // "Key ="
                line = $"{left} {oldValue}";
            }

            sb.Append(line);
            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>Parses effective <c>Key = value</c> pairs from a .conf, ignoring comments/blank lines.</summary>
    private static Dictionary<string, string> ParseConfValues(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (TryParseKeyLine(line, out var key, out var eqIndex))
            {
                result[key] = line[(eqIndex + 1)..].Trim();
            }
        }
        return result;
    }

    /// <summary>
    /// True when a line is an assignment (<c>Key = value</c>) with a well-formed key. Outputs the key
    /// and the index of the first '='. Comments (leading '#') and non-assignment lines return false.
    /// </summary>
    private static bool TryParseKeyLine(string line, out string key, out int eqIndex)
    {
        key = string.Empty;
        eqIndex = -1;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return false;
        }

        var idx = line.IndexOf('=');
        if (idx <= 0)
        {
            return false;
        }

        var keyPart = line[..idx].Trim();
        if (keyPart.Length == 0)
        {
            return false;
        }

        // AzerothCore config keys are dotted identifiers (e.g. Updates.EnableDatabases); reject anything
        // with whitespace or unexpected characters so we never rewrite prose or section banners.
        foreach (var c in keyPart)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))
            {
                return false;
            }
        }

        key = keyPart;
        eqIndex = idx;
        return true;
    }
}
