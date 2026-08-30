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
/// mirrored to the manager at <c>{BuildsPath}/{stackId}/azerothcore-wotlk/env/dist/etc</c>. Missing
/// effective <c>*.conf</c> files are copied from <c>*.conf.dist</c> (image or checkout) so Express
/// Server Wide Progression can edit <c>worldserver.conf</c> before the game servers generate it.
/// Edits are written to the <c>etc</c> volume immediately so a worldserver restart
/// picks them up (a full stack Start also re-seeds the mirror).
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
        await EnsureSeededAsync(stackId, cancellationToken);
        var dto = new ServerConfigListDto { StackId = stackId };

        if (!Directory.Exists(etcDir))
        {
            return dto;
        }

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
        await EnsureSeededAsync(stackId, cancellationToken);
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
        SeedMissingEffectiveConfigs(etcDir);
        var target = SafeResolveConf(etcDir, relativePath);
        if (!File.Exists(target))
        {
            throw new FileNotFoundException($"Config file not found: {relativePath}");
        }

        await File.WriteAllTextAsync(target, content, cancellationToken);
        _logger.LogInformation("Updated config {Path} for stack {StackId}", relativePath, stackId);
        await PushConfToEtcVolumeAsync(stackId, target, cancellationToken);

        return await ListAsync(stackId, cancellationToken);
    }

    /// <summary>
    /// Copies one saved .conf into the stack etc volume. Worldserver mounts that volume, not the
    /// manager mirror, so a UI save must reach the volume or a container-only restart keeps the old
    /// values.
    /// </summary>
    private async Task PushConfToEtcVolumeAsync(string stackId, string localPath, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return;
        }

        var volume = DockerComposeOverrideGenerator.EtcVolumeName(stackId);
        if (!await _remoteEngine.VolumeExistsAsync(stack, volume, cancellationToken))
        {
            return;
        }

        var relative = NormalizeRelative(GetEtcDir(stackId), localPath);
        await using var stream = File.OpenRead(localPath);
        await _remoteEngine.WriteVolumeFileFromStreamAsync(stack, volume, relative, stream, cancellationToken);
        _logger.LogInformation(
            "Pushed config {Path} into etc volume {Volume} for stack {StackId}. Restart worldserver to apply.",
            relative,
            volume,
            stackId);
    }

    public async Task EnsureSeededAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var etcDir = GetEtcDir(stackId);
        Directory.CreateDirectory(etcDir);
        var repoPath = Path.Combine(_buildsPath, stackId, "azerothcore-wotlk");
        ModuleSidecarConf.SeedFromCheckouts(etcDir, Path.Combine(repoPath, "modules"));
        CopyCheckoutServerDists(repoPath, etcDir);
        await SupplementMissingConfigsFromImageAsync(stackId, etcDir, cancellationToken);
        var seeded = SeedMissingEffectiveConfigs(etcDir);
        if (seeded > 0)
        {
            _logger.LogInformation(
                "Materialized {Count} .conf file(s) from .conf.dist for stack {StackId}",
                seeded, stackId);
        }
    }

    // ===== Helpers =====

    /// <summary>
    /// Populates missing files in the manager's local etc mirror from the stack's <c>etc</c> named
    /// volume. Skipped once <c>worldserver.conf</c> is present. Module confs are seeded at stack start,
    /// so a naive "any conf exists" check would skip the fetch and leave worldserver.conf missing —
    /// Express Server Wide Progression bootstrap then fails. Only copies files that are not already
    /// local so operator edits are never clobbered. Best-effort: any failure leaves the local mirror as-is.
    /// </summary>
    private async Task SyncEtcFromVolumeAsync(string stackId, string etcDir, CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(etcDir, "worldserver.conf")))
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

        var tmpRoot = NewStackTempDir(stackId, "etc-volume-tmp");
        try
        {
            var volume = DockerComposeOverrideGenerator.EtcVolumeName(stackId);
            if (!await _remoteEngine.VolumeExistsAsync(stack, volume, cancellationToken))
            {
                return;
            }

            if (Directory.Exists(tmpRoot))
            {
                Directory.Delete(tmpRoot, recursive: true);
            }

            Directory.CreateDirectory(tmpRoot);
            await _remoteEngine.FetchVolumeAsync(stack, volume, tmpRoot, cancellationToken);
            Directory.CreateDirectory(etcDir);
            var copied = CopyMissingFiles(tmpRoot, etcDir);
            if (copied > 0)
            {
                _logger.LogInformation(
                    "Copied {Count} missing config file(s) from the etc volume for stack {StackId}",
                    copied, stackId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync config from the etc volume for stack {StackId}", stackId);
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

    /// <summary>True when a config lives under the <c>modules/</c> subdirectory of etc.</summary>
    private static bool IsModuleConfig(string relativePath) =>
        relativePath.StartsWith("modules/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For each <c>*.conf.dist</c> without a sibling <c>*.conf</c> (worldserver, authserver, and
    /// modules), copies the .dist so Express / Server Wide Progression can edit the effective file
    /// before worldserver has generated one. Idempotent.
    /// </summary>
    internal static int SeedMissingEffectiveConfigs(string etcDir)
    {
        if (!Directory.Exists(etcDir))
        {
            return 0;
        }

        var seeded = 0;
        foreach (var dist in Directory.EnumerateFiles(etcDir, "*.conf.dist", SearchOption.AllDirectories))
        {
            var conf = dist[..^".dist".Length];
            if (File.Exists(conf))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(conf)!);
            File.Copy(dist, conf);
            seeded++;
        }

        return seeded;
    }

    /// <summary>Copies files from <paramref name="sourceDir"/> that are not already in <paramref name="destDir"/>.</summary>
    internal static int CopyMissingFiles(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            return 0;
        }

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, relative);
            if (File.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Copies <c>worldserver.conf.dist</c> / <c>authserver.conf.dist</c> from the AzerothCore checkout
    /// when the image has not been extracted yet. Does not overwrite existing files.
    /// </summary>
    internal static int CopyCheckoutServerDists(string repoPath, string etcDir)
    {
        if (!Directory.Exists(repoPath))
        {
            return 0;
        }

        Directory.CreateDirectory(etcDir);
        var copied = 0;
        copied += CopyDistTree(Path.Combine(repoPath, "env", "ref", "etc"), etcDir, flatten: false);
        copied += CopyDistTree(Path.Combine(repoPath, "src", "server", "apps", "worldserver"), etcDir, flatten: true);
        copied += CopyDistTree(Path.Combine(repoPath, "src", "server", "apps", "authserver"), etcDir, flatten: true);
        return copied;
    }

    private static int CopyDistTree(string sourceDir, string etcDir, bool flatten)
    {
        if (!Directory.Exists(sourceDir))
        {
            return 0;
        }

        var copied = 0;
        var option = flatten ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
        foreach (var dist in Directory.EnumerateFiles(sourceDir, "*.conf.dist", option))
        {
            var relative = flatten ? Path.GetFileName(dist) : Path.GetRelativePath(sourceDir, dist);
            var target = Path.Combine(etcDir, relative);
            if (File.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(dist, target);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Copies any <c>*.conf.dist</c> templates from the stack's worldserver image that are not yet
    /// present in the local etc mirror (e.g. after adding a module when config migration used the wrong
    /// image path). Best-effort; a stack restart seeds these into the live etc volume.
    /// </summary>
    private async Task SupplementMissingConfigsFromImageAsync(string stackId, string etcDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(etcDir);
        var tmpRoot = NewStackTempDir(stackId, "config-image-tmp");
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
                    && HasMatchingFiles(tmpRoot, "*.conf.dist"))
                {
                    extracted = true;
                    break;
                }
            }

            if (!extracted || !Directory.Exists(tmpRoot))
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

    /// <summary>
    /// Unique, fully-resolved temp dir under the stack root. Must not use <c>etc/../name</c>: on first
    /// start <c>etc</c> does not exist yet, so <c>..</c> cannot be walked and enumeration throws.
    /// </summary>
    private string NewStackTempDir(string stackId, string prefix) =>
        ResolveStackTempDir(_buildsPath, stackId, $"{prefix}-{Guid.NewGuid():N}");

    internal static string ResolveStackTempDir(string buildsPath, string stackId, string name) =>
        Path.GetFullPath(Path.Combine(buildsPath, stackId, name));

    /// <summary>
    /// True when <paramref name="directory"/> exists and contains at least one file matching
    /// <paramref name="searchPattern"/>. Safe to call when the directory was never created.
    /// </summary>
    internal static bool HasMatchingFiles(string directory, string searchPattern) =>
        Directory.Exists(directory)
        && Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories).Any();

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
