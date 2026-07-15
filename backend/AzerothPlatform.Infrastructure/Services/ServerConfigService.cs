using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Reads/writes the server .conf files that live in a stack's pre-seeded <c>etc</c> named volume,
/// mirrored to the manager at <c>{BuildsPath}/{stackId}/azerothcore-wotlk/env/dist/etc</c>. The container
/// entrypoint seeds the effective configs into the volume on first start; this service fetches that
/// volume back to the manager on demand so operators can edit the configs. Edits are re-seeded into the
/// volume when the stack (re)starts; changes take effect after the game servers restart.
/// </summary>
public sealed class ServerConfigService : IServerConfigService
{
    private static readonly string EtcRelative = Path.Combine("azerothcore-wotlk", "env", "dist", "etc");
    private static readonly string[] ImageEtcPaths =
    [
        "/azerothcore/env/ref/etc",
        "/azerothcore/env/dist/etc",
    ];

    private readonly string _buildsPath;
    private readonly ILogger<ServerConfigService> _logger;
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IRemoteEngineService _remoteEngine;

    public ServerConfigService(
        IOptions<DockerOptions> dockerOptions,
        ILogger<ServerConfigService> logger,
        AzerothCoreDbContext dbContext,
        IRemoteEngineService remoteEngine)
    {
        var buildsPath = dockerOptions.Value.BuildsPath;
        _buildsPath = Path.IsPathRooted(buildsPath) ? buildsPath : Path.GetFullPath(buildsPath);
        _logger = logger;
        _dbContext = dbContext;
        _remoteEngine = remoteEngine;
    }

    public async Task<ServerConfigListDto> ListAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var etcDir = GetEtcDir(stackId);
        await SyncEtcFromVolumeAsync(stackId, etcDir, cancellationToken);
        await SupplementMissingConfigsFromImageAsync(stackId, etcDir, cancellationToken);
        var dto = new ServerConfigListDto { StackId = stackId };

        if (!Directory.Exists(etcDir))
        {
            return dto;
        }

        // Modules ship a <module>.conf.dist but (unlike worldserver/authserver) the container does
        // not seed an effective <module>.conf. Materialize it so installed modules' configs show up
        // and can be edited; the server reads the .conf and falls back to .conf.dist otherwise.
        SeedMissingModuleConfigs(etcDir);

        // Editable config files are the effective *.conf (not the *.conf.dist references).
        var files = Directory.EnumerateFiles(etcDir, "*.conf", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".conf.dist", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        dto.Generated = files.Count > 0;
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var relative = NormalizeRelative(etcDir, file);
            dto.Files.Add(new ServerConfigFileDto
            {
                Path = relative,
                Size = info.Length,
                ModifiedAt = info.LastWriteTimeUtc,
                Category = IsModuleConfig(relative) ? "modules" : "server"
            });
        }

        return dto;
    }

    public async Task<ServerConfigContentDto> ReadAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var etcDir = GetEtcDir(stackId);
        await SyncEtcFromVolumeAsync(stackId, etcDir, cancellationToken);
        await SupplementMissingConfigsFromImageAsync(stackId, etcDir, cancellationToken);
        var target = SafeResolveConf(etcDir, relativePath);
        if (!File.Exists(target))
        {
            throw new FileNotFoundException($"Config file not found: {relativePath}");
        }

        return new ServerConfigContentDto
        {
            Path = NormalizeRelative(etcDir, target),
            Content = File.ReadAllText(target)
        };
    }

    public async Task<ServerConfigListDto> SaveAsync(string stackId, string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var etcDir = GetEtcDir(stackId);
        var target = SafeResolveConf(etcDir, relativePath);
        if (!File.Exists(target))
        {
            // Only allow editing files the server already generated, to avoid creating stray configs.
            throw new FileNotFoundException($"Config file not found: {relativePath}");
        }

        await File.WriteAllTextAsync(target, content, cancellationToken);
        _logger.LogInformation("Updated config {Path} for stack {StackId}", relativePath, stackId);

        return await ListAsync(stackId, cancellationToken);
    }

    // ===== Helpers =====

    /// <summary>
    /// Populates the manager's local etc mirror from the stack's <c>etc</c> named volume the first time
    /// (when the local dir has no config files yet). The container seeds the effective configs into the
    /// volume on first start; this brings them back so they can be listed/edited. Skipped once the local
    /// mirror has content so operator edits are never clobbered (edits are re-seeded on the next start).
    /// Best-effort: any failure leaves the local mirror as-is.
    /// </summary>
    private async Task SyncEtcFromVolumeAsync(string stackId, string etcDir, CancellationToken cancellationToken)
    {
        if (HasAnyConf(etcDir))
        {
            return;
        }

        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return;
        }

        try
        {
            var volume = DockerComposeOverrideGenerator.EtcVolumeName(stackId);
            if (!await _remoteEngine.VolumeExistsAsync(stack, volume, cancellationToken))
            {
                return;
            }

            Directory.CreateDirectory(etcDir);
            await _remoteEngine.FetchVolumeAsync(stack, volume, etcDir, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync config from the etc volume for stack {StackId}", stackId);
        }
    }

    /// <summary>True when the etc dir already holds at least one .conf or .conf.dist file.</summary>
    private static bool HasAnyConf(string etcDir) =>
        Directory.Exists(etcDir)
        && Directory.EnumerateFiles(etcDir, "*.conf*", SearchOption.AllDirectories).Any();

    /// <summary>True when a config lives under the <c>modules/</c> subdirectory of etc.</summary>
    private static bool IsModuleConfig(string relativePath) =>
        relativePath.StartsWith("modules/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For each <c>modules/*.conf.dist</c> without a sibling <c>*.conf</c>, copies the .dist to a
    /// .conf so the module's configuration is present and editable. Idempotent and best-effort.
    /// </summary>
    private void SeedMissingModuleConfigs(string etcDir)
    {
        var modulesDir = Path.Combine(etcDir, "modules");
        if (!Directory.Exists(modulesDir))
        {
            return;
        }

        foreach (var dist in Directory.EnumerateFiles(modulesDir, "*.conf.dist", SearchOption.AllDirectories))
        {
            // "playerbots.conf.dist" -> "playerbots.conf"
            var conf = dist[..^".dist".Length];
            if (File.Exists(conf))
            {
                continue;
            }

            try
            {
                File.Copy(dist, conf);
                _logger.LogInformation("Seeded module config {Conf} from {Dist}", Path.GetFileName(conf), Path.GetFileName(dist));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed module config from {Dist}", dist);
            }
        }
    }

    /// <summary>
    /// Copies any <c>*.conf.dist</c> templates from the stack's worldserver image that are not yet
    /// present in the local etc mirror (e.g. after adding a module when config migration used the wrong
    /// image path). Best-effort; a stack restart seeds these into the live etc volume.
    /// </summary>
    private async Task SupplementMissingConfigsFromImageAsync(string stackId, string etcDir, CancellationToken cancellationToken)
    {
        var tmpRoot = Path.Combine(etcDir, "..", "config-image-tmp");
        try
        {
            var image = $"acore/ac-wotlk-worldserver:{stackId}";
            var extracted = false;
            foreach (var imagePath in ImageEtcPaths)
            {
                if (Directory.Exists(tmpRoot))
                {
                    Directory.Delete(tmpRoot, recursive: true);
                }

                if (await _remoteEngine.ExtractImageDirAsync(image, imagePath, tmpRoot, cancellationToken)
                    && Directory.EnumerateFiles(tmpRoot, "*.conf.dist", SearchOption.AllDirectories).Any())
                {
                    extracted = true;
                    break;
                }
            }

            if (!extracted)
            {
                return;
            }

            Directory.CreateDirectory(etcDir);
            var copied = 0;
            foreach (var dist in Directory.EnumerateFiles(tmpRoot, "*.conf.dist", SearchOption.AllDirectories))
            {
                var relative = NormalizeRelative(tmpRoot, dist);
                var target = Path.Combine(etcDir, relative);
                var effective = target[..^".dist".Length];
                if (File.Exists(target) || File.Exists(effective))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(dist, target);
                copied++;
            }

            if (copied > 0)
            {
                _logger.LogInformation(
                    "Supplemented {Count} missing config file(s) from image for stack {StackId}",
                    copied, stackId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to supplement missing configs from image for stack {StackId}", stackId);
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

    private string GetEtcDir(string stackId)
    {
        if (string.IsNullOrWhiteSpace(stackId) || stackId.Contains('/') || stackId.Contains('\\') || stackId.Contains(".."))
        {
            throw new ArgumentException($"Invalid stack id: {stackId}");
        }
        return Path.Combine(_buildsPath, stackId, EtcRelative);
    }

    private static string NormalizeRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static string SafeResolveConf(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A config file path is required.");
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (!normalized.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".conf.dist", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .conf files can be edited.");
        }

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, normalized));

        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid config path: {relativePath}");
        }

        return candidate;
    }
}
