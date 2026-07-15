using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Migrations;

/// <summary>
/// Builds a <see cref="ClientDistributionContext"/> for a stack's per-stack client root and delegates
/// to <see cref="IClientDistributionService"/> to serve it.
/// </summary>
public sealed class StackLauncherService : IStackLauncherService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly DockerOptions _dockerOptions;
    private readonly MigrationOptions _migrationOptions;
    private readonly IClientDistributionService _clientDistribution;
    private readonly IClientContainerService _clientContainer;

    public StackLauncherService(
        AzerothCoreDbContext dbContext,
        IOptions<DockerOptions> dockerOptions,
        IOptions<MigrationOptions> migrationOptions,
        IClientDistributionService clientDistribution,
        IClientContainerService clientContainer)
    {
        _dbContext = dbContext;
        _dockerOptions = dockerOptions.Value;
        _migrationOptions = migrationOptions.Value;
        _clientDistribution = clientDistribution;
        _clientContainer = clientContainer;
    }

    public async Task EnsureLauncherVisibleAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var visible = await _dbContext.ManagedStacks
            .Where(s => s.Id == stackId && s.LauncherVisible)
            .AnyAsync(cancellationToken);
        if (!visible)
        {
            // Same error a missing stack produces, so a hidden stack cannot be distinguished from a
            // non-existent one via the anonymous endpoints.
            throw new KeyNotFoundException($"Stack not found: {stackId}");
        }
    }

    public async Task<LauncherConfigDto> GetConfigAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        var context = BuildContext(stack);
        var config = await _clientDistribution.GetLauncherConfigAsync(context, cancellationToken);
        config.ClientContentBaseUrl = BuildClientContentBaseUrl(stack);
        return config;
    }

    /// <summary>
    /// Absolute base URL of this stack's client-server container as reached by players: the same
    /// public host they use for realmlist, on the stack's published client port. Blank when the stack
    /// has no client container (no port allocated / client disabled), so the launcher falls back to the
    /// manager's legacy per-stack file endpoints.
    /// </summary>
    private string BuildClientContentBaseUrl(Data.Entities.ManagedStackEntity stack)
    {
        if (!stack.ClientEnabled || stack.ClientPort <= 0)
        {
            return string.Empty;
        }

        var host = string.IsNullOrWhiteSpace(stack.ExternalHost)
            ? (string.IsNullOrWhiteSpace(stack.RealmlistHostOverride)
                ? _migrationOptions.RealmlistHost
                : stack.RealmlistHostOverride)
            : stack.ExternalHost;

        if (string.IsNullOrWhiteSpace(host))
        {
            host = "127.0.0.1";
        }

        // Secure by default (see MigrationOptions.ClientContentScheme). "auto" keeps plain http for
        // loopback/private LAN (no TLS on the client port there) and uses https for public hosts so an
        // internet-facing deployment never advertises a plaintext client URL.
        var configured = _migrationOptions.ClientContentScheme?.Trim();
        var scheme = configured?.ToLowerInvariant() switch
        {
            "http" => "http",
            "https" => "https",
            _ => IsPrivateOrLoopbackHost(host) ? "http" : "https",
        };
        return $"{scheme}://{host}:{stack.ClientPort}";
    }

    /// <summary>
    /// True when <paramref name="host"/> is loopback or an RFC1918 / link-local private address (or a
    /// ".local" name) — i.e. not reachable from the public internet, so a plain-http client URL is safe.
    /// </summary>
    private static bool IsPrivateOrLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254);
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.GetAddressBytes()[0] == 0xfd;
            }
        }

        return false;
    }

    public async Task<ClientManifest> GetManifestAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var context = await BuildContextAsync(stackId, cancellationToken);
        return await _clientDistribution.GetManifestAsync(context, cancellationToken);
    }

    public async Task<ClientManifest> RescanAsync(string stackId, CancellationToken cancellationToken = default)
    {
        // The client-server container now owns the manifest: trigger its rescan, then return the freshly
        // rebuilt manifest it serves.
        await _clientContainer.RescanAsync(stackId, cancellationToken);
        return await GetManifestAsync(stackId, cancellationToken);
    }

    public async Task<ClientManifest> ForceVerifyAsync(string stackId, CancellationToken cancellationToken = default)
    {
        await _clientContainer.ForceVerifyAsync(stackId, cancellationToken);
        return await GetManifestAsync(stackId, cancellationToken);
    }

    public async Task<ClientManifestRebuildResultDto> RebuildManifestAsync(string stackId, CancellationToken cancellationToken = default)
    {
        // Re-hash on the stack's client-server container (what launchers fetch) and bump the verify
        // token so every client full-syncs. Uses the long-standing /rescan + /force-verify routes so
        // this works before the client-server image picks up /rebuild-manifest.
        await _clientContainer.RescanAsync(stackId, cancellationToken);
        await _clientContainer.ForceVerifyAsync(stackId, cancellationToken);

        // Rebuild the manager-side manifest too so the admin UI reflects corrected base/managed groups.
        var context = await BuildContextAsync(stackId, cancellationToken);
        var manifest = await _clientDistribution.RescanAsync(context, cancellationToken);
        return ToRebuildResult(manifest);
    }

    private static ClientManifestRebuildResultDto ToRebuildResult(ClientManifest manifest)
    {
        var baseFiles = manifest.Files.Where(f => f.Group == ManifestFileGroup.Base).ToList();
        var managedFiles = manifest.Files.Where(f => f.Group == ManifestFileGroup.Managed).ToList();
        return new ClientManifestRebuildResultDto
        {
            Version = manifest.Version,
            VerifyToken = manifest.VerifyToken,
            FileCount = manifest.Files.Count,
            TotalSize = manifest.TotalSize,
            BaseFileCount = baseFiles.Count,
            BaseTotalSize = baseFiles.Sum(f => f.Size),
            ManagedFileCount = managedFiles.Count,
            ManagedTotalSize = managedFiles.Sum(f => f.Size),
        };
    }

    // File name of the Config.wtf template in a stack's client/settings dir ("__" => path separator,
    // ".once" => write-only-if-missing). Matches client-example/settings and ParseTemplateName.
    private const string ConfigTemplateFileName = "WTF__Config.wtf.once.tmpl";

    public async Task<string> GetConfigTemplateAsync(string stackId, CancellationToken cancellationToken = default)
    {
        await EnsureStackExistsAsync(stackId, cancellationToken);

        var stackTemplate = Path.Combine(ClientSettingsDir(stackId), ConfigTemplateFileName);
        if (File.Exists(stackTemplate))
        {
            return await File.ReadAllTextAsync(stackTemplate, cancellationToken);
        }

        // Not seeded yet: show the baked default so the admin edits from the real starting point.
        var seedTemplate = Path.Combine(_migrationOptions.ClientSettingsTemplatePath, ConfigTemplateFileName);
        if (File.Exists(seedTemplate))
        {
            return await File.ReadAllTextAsync(seedTemplate, cancellationToken);
        }

        return "SET realmList \"{{HOST}}:{{PORT}}\"\n";
    }

    public async Task SaveConfigTemplateAsync(string stackId, string content, CancellationToken cancellationToken = default)
    {
        await EnsureStackExistsAsync(stackId, cancellationToken);

        var dir = ClientSettingsDir(stackId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, ConfigTemplateFileName), content ?? string.Empty, cancellationToken);
    }

    private async Task EnsureStackExistsAsync(string stackId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.ManagedStacks.AnyAsync(s => s.Id == stackId, cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException($"Stack not found: {stackId}");
        }
    }

    /// <summary>Per-stack client settings templates dir (<c>{BuildsPath}/{stackId}/client/settings</c>).</summary>
    private string ClientSettingsDir(string stackId)
    {
        var baseDir = Path.IsPathRooted(_dockerOptions.BuildsPath)
            ? _dockerOptions.BuildsPath
            : Path.GetFullPath(_dockerOptions.BuildsPath);
        return MigrationLayout.ClientSettingsDir(Path.Combine(baseDir, stackId));
    }

    public async Task<string?> ResolveFilePathAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var context = await BuildContextAsync(stackId, cancellationToken);
        return _clientDistribution.ResolveFilePath(context, relativePath);
    }

    private async Task<ClientDistributionContext> BuildContextAsync(string stackId, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        return BuildContext(stack);
    }

    private ClientDistributionContext BuildContext(Data.Entities.ManagedStackEntity stack)
    {
        var stackId = stack.Id;

        var baseDir = Path.IsPathRooted(_dockerOptions.BuildsPath)
            ? _dockerOptions.BuildsPath
            : Path.GetFullPath(_dockerOptions.BuildsPath);

        var clientRoot = Path.Combine(baseDir, stackId, MigrationLayout.ClientDirName);

        var realmlistHost = string.IsNullOrWhiteSpace(stack.RealmlistHostOverride)
            ? _migrationOptions.RealmlistHost
            : stack.RealmlistHostOverride;

        return new ClientDistributionContext
        {
            RootPath = clientRoot,
            RealmlistHost = realmlistHost,
            RealmlistPort = stack.AuthServerPort,
            BrandingTitle = string.IsNullOrWhiteSpace(stack.LauncherDisplayName)
                ? (string.IsNullOrWhiteSpace(stack.RealmName) ? "Azeroth Platform Launcher" : stack.RealmName)
                : stack.LauncherDisplayName
        };
    }
}
