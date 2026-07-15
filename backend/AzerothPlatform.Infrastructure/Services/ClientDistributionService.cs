using System.Collections.Concurrent;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Generates and serves WoW client manifests and launcher configuration from files placed under a
/// client root. Works for both the global client root (<see cref="ClientDistributionOptions"/>) and
/// per-stack roots via <see cref="ClientDistributionContext"/>.
/// SHA-256 hashes are cached by path + size + mtime so multi-GB clients are not rehashed on every request.
/// </summary>
public sealed class ClientDistributionService : IClientDistributionService
{
    private const string GameDirectoryName = "game";
    private const string SettingsDirectoryName = "settings";
    private const string LauncherConfigFileName = "launcher.json";
    private const string VerifyTokenFileName = ".verifytoken";
    private const string TemplateExtension = ".tmpl";
    private const string OnceMarker = ".once";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ClientDistributionOptions _options;
    private readonly ILogger<ClientDistributionService> _logger;
    private readonly IManifestSigningKeyProvider _signingKeys;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Manifest cache keyed by client root path (supports the global root and many per-stack roots).
    private readonly ConcurrentDictionary<string, ClientManifest> _manifestCache = new(StringComparer.Ordinal);

    public ClientDistributionService(
        IOptions<ClientDistributionOptions> options,
        ILogger<ClientDistributionService> logger,
        IManifestSigningKeyProvider signingKeys)
    {
        _options = options.Value;
        _logger = logger;
        _signingKeys = signingKeys;
    }

    /// <summary>Builds the context describing the global client root from configuration.</summary>
    private ClientDistributionContext GlobalContext => new()
    {
        RootPath = _options.RootPath,
        GameExecutable = _options.GameExecutable,
        LaunchArguments = _options.LaunchArguments,
        ClientVersion = _options.ClientVersion,
        BrandingTitle = _options.BrandingTitle,
        RealmlistHost = _options.Realmlist.Host,
        RealmlistPort = _options.Realmlist.Port,
        ManagedPrefixes = _options.ManagedPrefixes
    };

    // ===== Global (backward-compatible) API =====

    public Task<ClientManifest> GetManifestAsync(CancellationToken cancellationToken = default)
        => GetManifestAsync(GlobalContext, cancellationToken);

    public Task<ClientManifest> RescanAsync(CancellationToken cancellationToken = default)
        => RescanAsync(GlobalContext, cancellationToken);

    public Task<LauncherConfigDto> GetLauncherConfigAsync(CancellationToken cancellationToken = default)
        => GetLauncherConfigAsync(GlobalContext, cancellationToken);

    public string? ResolveFilePath(string relativePath)
        => ResolveFilePath(GlobalContext, relativePath);

    // ===== Context-scoped API =====

    public async Task<ClientManifest> GetManifestAsync(ClientDistributionContext context, CancellationToken cancellationToken = default)
    {
        if (_manifestCache.TryGetValue(context.RootPath, out var cached))
        {
            return cached;
        }

        return await BuildManifestAsync(context, cancellationToken);
    }

    public async Task<ClientManifest> RescanAsync(ClientDistributionContext context, CancellationToken cancellationToken = default)
    {
        _manifestCache.TryRemove(context.RootPath, out _);
        ClearHashCache(context);
        return await BuildManifestAsync(context, cancellationToken);
    }

    public Task<ClientManifest> ForceVerifyAsync(CancellationToken cancellationToken = default)
        => ForceVerifyAsync(GlobalContext, cancellationToken);

    public async Task<ClientManifest> ForceVerifyAsync(ClientDistributionContext context, CancellationToken cancellationToken = default)
    {
        // Persist a fresh token so every launcher notices the change on its next check and full-verifies
        // (re-hashes) all files, even when the manifest content hash is otherwise unchanged.
        WriteVerifyToken(context.RootPath);
        _manifestCache.TryRemove(context.RootPath, out _);
        ClearHashCache(context);
        return await BuildManifestAsync(context, cancellationToken);
    }

    public async Task<LauncherConfigDto> GetLauncherConfigAsync(ClientDistributionContext context, CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(context, cancellationToken);
        var overrides = LoadOverrides(context.RootPath);

        var host = overrides?.RealmlistHost ?? context.RealmlistHost;
        var port = overrides?.RealmlistPort ?? context.RealmlistPort;

        return new LauncherConfigDto
        {
            GameExecutable = overrides?.GameExecutable ?? context.GameExecutable,
            LaunchArguments = overrides?.LaunchArguments ?? context.LaunchArguments,
            ClientVersion = overrides?.ClientVersion ?? context.ClientVersion,
            BrandingTitle = overrides?.BrandingTitle ?? context.BrandingTitle,
            RealmlistHost = host,
            RealmlistPort = port,
            ManifestVersion = manifest.Version,
            ClientManifestPublicKey = _signingKeys.PublicKeySpkiBase64,
            Settings = await RenderSettingsAsync(context.RootPath, host, port, cancellationToken)
        };
    }

    public string? ResolveFilePath(ClientDistributionContext context, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var gameRoot = Path.GetFullPath(Path.Combine(context.RootPath, GameDirectoryName));
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        // Never serve the manifest builder's own bookkeeping sidecars. They live under game/ but are
        // excluded from the (signed) manifest, so serving them would leak the full file inventory and
        // per-file SHA-256 hashes to unauthenticated callers. Match the client-server whitelist behaviour.
        var fileName = Path.GetFileName(normalized);
        if (fileName.Equals(global::AzerothPlatform.ClientContent.ClientManifestBuilder.HashCacheFileName, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(global::AzerothPlatform.ClientContent.ClientManifestBuilder.ManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Rejected client file request for manifest sidecar: {RelativePath}", relativePath);
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(gameRoot, normalized));

        var rootWithSep = gameRoot.EndsWith(Path.DirectorySeparatorChar)
            ? gameRoot
            : gameRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected client file request outside game root: {RelativePath}", relativePath);
            return null;
        }

        return File.Exists(candidate) ? candidate : null;
    }

    private async Task<ClientManifest> BuildManifestAsync(ClientDistributionContext context, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_manifestCache.TryGetValue(context.RootPath, out var cached))
            {
                return cached;
            }

            var gameRoot = Path.Combine(context.RootPath, GameDirectoryName);
            if (!Directory.Exists(gameRoot))
            {
                _logger.LogWarning("Client game directory does not exist: {GameRoot}. Serving empty manifest.", gameRoot);
                var empty = new ClientManifest
                {
                    Version = "empty",
                    VerifyToken = ReadVerifyToken(context.RootPath),
                    GeneratedAt = DateTime.UtcNow,
                    TotalSize = 0,
                    Files = new List<ManifestFile>()
                };
                global::AzerothPlatform.ClientContent.ManifestSigner.Sign(empty, _signingKeys.PrivateKeyPkcs8Base64);
                _manifestCache[context.RootPath] = empty;
                return empty;
            }

            // Delegate the scan/hash/group/version work to the shared builder so the manager and the
            // standalone client-server container produce byte-identical manifests. Single game root,
            // with the hash cache + manifest snapshot persisted alongside the files as before.
            var result = await global::AzerothPlatform.ClientContent.ClientManifestBuilder.BuildAsync(
                gameRoots: new[] { gameRoot },
                cacheDirectory: gameRoot,
                managedPrefixes: context.ManagedPrefixes,
                verifyToken: ReadVerifyToken(context.RootPath),
                persistManifest: true,
                cancellationToken: cancellationToken,
                signingPrivateKey: _signingKeys.PrivateKeyPkcs8Base64);

            var manifest = result.Manifest;
            _manifestCache[context.RootPath] = manifest;
            _logger.LogInformation(
                "Built client manifest for {Root}: {FileCount} files, {TotalBytes} bytes, version {Version}.",
                context.RootPath, manifest.Files.Count, manifest.TotalSize, manifest.Version);

            return manifest;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<LauncherSettingsFileDto>> RenderSettingsAsync(
        string rootPath, string host, int port, CancellationToken cancellationToken)
    {
        var results = new List<LauncherSettingsFileDto>();
        var settingsDir = Path.Combine(rootPath, SettingsDirectoryName);
        if (!Directory.Exists(settingsDir))
        {
            return results;
        }

        foreach (var absolutePath in Directory.EnumerateFiles(settingsDir, "*" + TemplateExtension, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativeName = Path.GetRelativePath(settingsDir, absolutePath);
            var (targetPath, overwrite) = ParseTemplateName(relativeName);

            var content = await File.ReadAllTextAsync(absolutePath, cancellationToken);
            content = content
                .Replace("{{HOST}}", host, StringComparison.Ordinal)
                .Replace("{{PORT}}", port.ToString(), StringComparison.Ordinal);

            results.Add(new LauncherSettingsFileDto
            {
                TargetRelativePath = targetPath,
                Content = content,
                Overwrite = overwrite
            });
        }

        return results;
    }

    /// <summary>
    /// Template file names encode their destination: "__" becomes a path separator, the trailing
    /// ".tmpl" is stripped, and a ".once" marker (before ".tmpl") means write-only-if-missing.
    /// Example: "Data__enUS__realmlist.wtf.tmpl" -> ("Data/enUS/realmlist.wtf", overwrite: true).
    /// Example: "WTF__Config.wtf.once.tmpl" -> ("WTF/Config.wtf", overwrite: false).
    /// </summary>
    private static (string TargetPath, bool Overwrite) ParseTemplateName(string relativeName)
    {
        var name = relativeName.Replace('\\', '/');
        if (name.EndsWith(TemplateExtension, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^TemplateExtension.Length];
        }

        var overwrite = true;
        if (name.EndsWith(OnceMarker, StringComparison.OrdinalIgnoreCase))
        {
            overwrite = false;
            name = name[..^OnceMarker.Length];
        }

        var targetPath = name.Replace("__", "/");
        return (targetPath, overwrite);
    }

    private LauncherConfigOverrides? LoadOverrides(string rootPath)
    {
        var path = Path.Combine(rootPath, LauncherConfigFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LauncherConfigOverrides>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read launcher overrides at {Path}; using defaults.", path);
            return null;
        }
    }

    /// <summary>
    /// Reads the operator-controlled verify token from the client root (a small sidecar file next to
    /// <c>game/</c>, so it's never enumerated into the manifest). Empty when none has been set.
    /// </summary>
    private string ReadVerifyToken(string rootPath)
    {
        try
        {
            var path = Path.Combine(rootPath, VerifyTokenFileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read verify token at {Root}; treating as unset.", rootPath);
            return string.Empty;
        }
    }

    /// <summary>Writes a fresh verify token into the client root, bumping the forced-verify signal.</summary>
    private void WriteVerifyToken(string rootPath)
    {
        var path = Path.Combine(rootPath, VerifyTokenFileName);
        Directory.CreateDirectory(rootPath);
        File.WriteAllText(path, DateTime.UtcNow.Ticks.ToString());
    }

    private static void ClearHashCache(ClientDistributionContext context)
    {
        try
        {
            var gameRoot = Path.Combine(context.RootPath, GameDirectoryName);
            var path = Path.Combine(gameRoot, global::AzerothPlatform.ClientContent.ClientManifestBuilder.HashCacheFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Non-fatal; hashes are recomputed on the next scan.
        }
    }

    private sealed class LauncherConfigOverrides
    {
        public string? GameExecutable { get; set; }
        public string? LaunchArguments { get; set; }
        public string? ClientVersion { get; set; }
        public string? BrandingTitle { get; set; }
        public string? RealmlistHost { get; set; }
        public int? RealmlistPort { get; set; }
    }
}
