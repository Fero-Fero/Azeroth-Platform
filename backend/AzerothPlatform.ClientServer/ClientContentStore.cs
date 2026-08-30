using AzerothPlatform.ClientContent;
using AzerothPlatform.Core.Contracts;
using ClientManifestDto = AzerothPlatform.Core.Contracts.ClientManifest;

namespace AzerothPlatform.ClientServer;

/// <summary>
/// Options describing where the client-server finds its files, driven entirely by environment so the
/// same image serves any stack. The base volume is shared/read-only; the overlay volume is per-stack
/// read-write and overrides the base by relative path (this is where published patch MPQs land).
/// </summary>
public sealed class ClientContentOptions
{
    /// <summary>Read-only shared base client root (WoW install files map 1:1 under here).</summary>
    public string BaseRoot { get; set; } = "/client/base";

    /// <summary>Read-write per-stack overlay root (published MPQs / managed files land here).</summary>
    public string OverlayRoot { get; set; } = "/client/overlay";

    /// <summary>Writable directory for the hash cache, manifest snapshot and verify token.</summary>
    public string CacheDir { get; set; } = "/client/cache";

    /// <summary>Managed-file prefixes (comma separated in env), else the shared default.</summary>
    public List<string> ManagedPrefixes { get; set; } = new(ClientManifestBuilder.DefaultManagedPrefixes);

    /// <summary>Bearer token guarding the mutating endpoints (/rescan, /force-verify). Blank disables auth.</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Base64 PKCS#8 ECDSA private key used to sign the manifest. Provisioned by the manager (identical
    /// across all of a platform's client-servers) so launchers verify against one public key. Blank
    /// disables signing.
    /// </summary>
    public string ManifestPrivateKey { get; set; } = string.Empty;

    /// <summary>Directory (mounted from the per-stack launcher-dist volume) holding the launcher exe + build.json.</summary>
    public string LauncherDistDir { get; set; } = "/launcher-dist";

    /// <summary>Whether the container verifies player logins against the stack auth DB (POST /login).</summary>
    public bool LoginEnabled { get; set; }

    public string DbHost { get; set; } = "host.docker.internal";
    public int DbPort { get; set; } = 3306;
    public string DbUser { get; set; } = "root";
    public string DbPassword { get; set; } = string.Empty;
    public string AuthDatabase { get; set; } = "acore_auth";

    /// <summary>This stack's identity + advertised connection info, used to render a fallback /portal
    /// document before the manager pushes the full registry (and to mark the self entry).</summary>
    public string StackId { get; set; } = string.Empty;
    public string AppName { get; set; } = "Azeroth Platform";
    public string DisplayName { get; set; } = string.Empty;
    public string RealmlistHost { get; set; } = string.Empty;
    public int RealmlistPort { get; set; }
    public int ArmoryPort { get; set; }
    public string Template { get; set; } = string.Empty;
    public string AccentColor { get; set; } = string.Empty;
    public bool RequireLogin { get; set; }

    public static ClientContentOptions FromEnvironment(IConfiguration config)
    {
        var options = new ClientContentOptions
        {
            BaseRoot = config["CLIENT_BASE_ROOT"] ?? "/client/base",
            OverlayRoot = config["CLIENT_OVERLAY_ROOT"] ?? "/client/overlay",
            CacheDir = config["CLIENT_CACHE_DIR"] ?? "/client/cache",
            AuthToken = (config["CLIENT_AUTH_TOKEN"] ?? string.Empty).Trim(),
            ManifestPrivateKey = config["CLIENT_MANIFEST_PRIVATE_KEY"] ?? string.Empty,
            LauncherDistDir = config["CLIENT_LAUNCHER_DIST_DIR"] ?? "/launcher-dist",
            LoginEnabled = ParseBool(config["CLIENT_LOGIN_ENABLED"], defaultValue: false),
            DbHost = config["CLIENT_DB_HOST"] ?? "host.docker.internal",
            DbPort = ParseInt(config["CLIENT_DB_PORT"], 3306),
            DbUser = config["CLIENT_DB_USER"] ?? "root",
            DbPassword = config["CLIENT_DB_PASSWORD"] ?? string.Empty,
            AuthDatabase = config["CLIENT_AUTH_DATABASE"] ?? "acore_auth",
            StackId = config["CLIENT_STACK_ID"] ?? string.Empty,
            AppName = config["CLIENT_APP_NAME"] ?? "Azeroth Platform",
            DisplayName = config["CLIENT_DISPLAY_NAME"] ?? string.Empty,
            RealmlistHost = config["CLIENT_REALMLIST_HOST"] ?? string.Empty,
            RealmlistPort = ParseInt(config["CLIENT_REALMLIST_PORT"], 0),
            ArmoryPort = ParseInt(config["CLIENT_ARMORY_PORT"], 0),
            Template = config["CLIENT_TEMPLATE"] ?? string.Empty,
            AccentColor = config["CLIENT_ACCENT_COLOR"] ?? string.Empty,
            RequireLogin = ParseBool(config["CLIENT_REQUIRE_LOGIN"], defaultValue: false),
        };

        var prefixes = config["CLIENT_MANAGED_PREFIXES"];
        if (!string.IsNullOrWhiteSpace(prefixes))
        {
            options.ManagedPrefixes = prefixes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return options;
    }

    private static bool ParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim() switch
        {
            "1" or "true" or "TRUE" or "True" or "yes" or "on" => true,
            "0" or "false" or "FALSE" or "False" or "no" or "off" => false,
            _ => defaultValue,
        };
    }

    private static int ParseInt(string? value, int defaultValue) =>
        int.TryParse(value, out var parsed) ? parsed : defaultValue;
}

/// <summary>
/// Holds the current manifest + path resolver and keeps them honest. The manifest is built lazily on
/// first request, rebuilt when <see cref="RescanAsync"/> is invoked (e.g. after a patch MPQ is pushed
/// into the overlay volume), and — crucially — rebuilt automatically when the files on disk stop
/// matching the ones it was built from.
///
/// That last part is what makes the served manifest trustworthy. The manager pokes /rescan after it
/// changes content, but that poke travels over <c>docker exec</c> and can fail (container stopped,
/// context unreachable) long after the admin has been told the change succeeded. Rather than trusting
/// it, every manifest request re-checks a cheap size/mtime fingerprint of both roots, so content
/// written by anyone — a failed-poke mutation, a patch apply, a hand-edited volume — is picked up
/// within seconds.
/// </summary>
public sealed class ClientContentStore
{
    private const string VerifyTokenFileName = ClientManifestBuilder.VerifyTokenFileName;

    /// <summary>
    /// How long a served manifest is trusted before the next request re-checks the on-disk shape. Short
    /// enough that an admin never sees a stale manifest in practice, long enough that a burst of
    /// launcher requests costs one directory walk rather than hundreds.
    /// </summary>
    private static readonly TimeSpan RevalidateInterval = TimeSpan.FromSeconds(5);

    private readonly ClientContentOptions _options;
    private readonly ILogger<ClientContentStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private ClientManifestResult? _current;
    private DateTime _lastValidatedUtc = DateTime.MinValue;
    private DateTime? _builtAtUtc;

    public ClientContentStore(ClientContentOptions options, ILogger<ClientContentStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current manifest, rebuilding it first if the files on disk no longer match the ones
    /// it was built from. The freshness check is a size/mtime directory walk (no hashing) rate-limited
    /// to <see cref="RevalidateInterval"/>, so correctness does not depend on anyone remembering — or
    /// managing — to call <see cref="RescanAsync"/> after changing content.
    /// </summary>
    public async Task<ClientManifestDto> GetManifestAsync(CancellationToken cancellationToken)
    {
        var current = _current;
        if (current is null)
        {
            return (await RebuildAsync(cancellationToken)).Manifest;
        }

        if (DateTime.UtcNow - _lastValidatedUtc < RevalidateInterval)
        {
            return current.Manifest;
        }

        if (!TryDetectContentChange(current.ShapeSignature, out var signature))
        {
            _lastValidatedUtc = DateTime.UtcNow;
            return current.Manifest;
        }

        _logger.LogInformation(
            "Client content changed on disk (shape {Previous} -> {Current}); rebuilding manifest.",
            Short(current.ShapeSignature), Short(signature));

        return (await RebuildAsync(cancellationToken)).Manifest;
    }

    /// <summary>
    /// Rebuilds the manifest from disk. The hash cache is kept: it is keyed by path + size + mtime, so
    /// changed files rehash themselves and unchanged multi-GB archives are not re-read. This is the
    /// cheap refresh that runs after every content mutation.
    /// </summary>
    public async Task<ClientManifestDto> RescanAsync(CancellationToken cancellationToken)
    {
        _current = null;
        return (await RebuildAsync(cancellationToken)).Manifest;
    }

    /// <summary>
    /// Rebuilds and bumps the verify token so every launcher re-hashes its local files on the next
    /// check. The server's own hash cache is kept — use <see cref="RebuildManifestAsync"/> to distrust
    /// that too.
    /// </summary>
    public async Task<ClientManifestDto> ForceVerifyAsync(CancellationToken cancellationToken)
    {
        WriteVerifyToken();
        _current = null;
        return (await RebuildAsync(cancellationToken)).Manifest;
    }

    /// <summary>
    /// Clears the hash cache, rebuilds the manifest from disk (re-hashing every file), and bumps the
    /// verify token so launchers full-sync on their next check.
    /// </summary>
    public async Task<ClientManifestDto> RebuildManifestAsync(CancellationToken cancellationToken)
    {
        WriteVerifyToken();
        ClearHashCache();
        _current = null;
        return (await RebuildAsync(cancellationToken)).Manifest;
    }

    /// <summary>
    /// Describes the manifest launchers are currently being served, refreshing it first if the content
    /// on disk has moved on. Exists so the manager's Client tab can report what players actually see
    /// rather than what the volume contains — the two diverge whenever propagation fails.
    /// </summary>
    public async Task<ClientManifestStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var manifest = await GetManifestAsync(cancellationToken);
        return new ClientManifestStatus
        {
            Version = manifest.Version,
            VerifyToken = manifest.VerifyToken ?? string.Empty,
            FileCount = manifest.Files.Count,
            BaseFileCount = manifest.Files.Count(f => f.Group == ManifestFileGroup.Base),
            ManagedFileCount = manifest.Files.Count(f => f.Group == ManifestFileGroup.Managed),
            TotalSize = manifest.TotalSize,
            BuiltAtUtc = _builtAtUtc,
            Signed = !string.IsNullOrWhiteSpace(manifest.Signature),
        };
    }

    /// <summary>Resolves a manifest path to the absolute backing file, or null if not part of the manifest.</summary>
    public string? ResolveFile(string relativePath)
    {
        var normalized = ClientManifestBuilder.ToManifestPath(relativePath).TrimStart('/');
        var files = _current?.Files;
        if (files is not null && files.TryGetValue(normalized, out var absolute) && File.Exists(absolute))
        {
            return absolute;
        }

        return null;
    }

    /// <summary>
    /// Recomputes the on-disk shape signature and reports whether it moved away from
    /// <paramref name="expected"/>. A scan failure is treated as "unchanged" so a transient IO error
    /// cannot stampede rebuilds; the next interval retries.
    /// </summary>
    private bool TryDetectContentChange(string expected, out string signature)
    {
        try
        {
            signature = ClientManifestBuilder.ComputeShapeSignature(
                new[] { _options.BaseRoot, _options.OverlayRoot });
            return !string.Equals(signature, expected, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check client content for changes; serving the cached manifest.");
            signature = expected;
            return false;
        }
    }

    private async Task<ClientManifestResult> RebuildAsync(CancellationToken cancellationToken)
    {
        var stale = _current;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Another request may have rebuilt while we queued on the lock. Reuse its result rather than
            // rescanning a multi-GB client again.
            var current = _current;
            if (current is not null && !ReferenceEquals(current, stale))
            {
                return current;
            }

            Directory.CreateDirectory(_options.CacheDir);
            var result = await ClientManifestBuilder.BuildAsync(
                gameRoots: new[] { _options.BaseRoot, _options.OverlayRoot },
                cacheDirectory: _options.CacheDir,
                managedPrefixes: _options.ManagedPrefixes,
                verifyToken: ReadVerifyToken(),
                persistManifest: true,
                cancellationToken: cancellationToken,
                signingPrivateKey: _options.ManifestPrivateKey);

            _current = result;
            _lastValidatedUtc = DateTime.UtcNow;
            _builtAtUtc = _lastValidatedUtc;
            _logger.LogInformation(
                "Built client manifest: {FileCount} files, {TotalBytes} bytes, version {Version}.",
                result.Manifest.Files.Count, result.Manifest.TotalSize, result.Manifest.Version);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string Short(string signature) =>
        signature.Length <= 12 ? signature : signature[..12];

    private string ReadVerifyToken()
    {
        try
        {
            var path = Path.Combine(_options.CacheDir, VerifyTokenFileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void WriteVerifyToken()
    {
        Directory.CreateDirectory(_options.CacheDir);
        var path = Path.Combine(_options.CacheDir, VerifyTokenFileName);
        File.WriteAllText(path, DateTime.UtcNow.Ticks.ToString());
    }

    private void ClearHashCache()
    {
        try
        {
            var path = Path.Combine(_options.CacheDir, ClientManifestBuilder.HashCacheFileName);
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
}
