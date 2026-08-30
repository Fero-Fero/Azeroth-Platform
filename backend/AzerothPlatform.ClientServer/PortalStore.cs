using System.Security.Cryptography;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.ClientServer;

/// <summary>
/// Serves and persists this stack's <c>/portal</c> document. The manager pushes the full replicated
/// registry snapshot as <c>portal.json</c> into the cache volume; this store reads it back and overlays
/// the launcher artifact info the container derives locally from its <c>launcher-dist</c> volume. When no
/// snapshot has been pushed yet, it renders a minimal fallback document from env so a freshly-provisioned
/// (e.g. VPC) stack still advertises itself and its launcher immediately.
/// </summary>
public sealed class PortalStore
{
    private const string PortalFileName = "portal.json";
    private const string BuildManifestFileName = "build.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ClientContentOptions _options;
    private readonly ILogger<PortalStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PortalStore(ClientContentOptions options, ILogger<PortalStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Builds the document served at <c>GET /portal</c> (persisted snapshot + local launcher info).</summary>
    public StackPortalDocument GetPortal()
    {
        var doc = ReadPersisted() ?? BuildFallback();

        // The container is authoritative for its own launcher artifact regardless of what the manager
        // last pushed (the exe lives in this stack's volume).
        var artifact = ReadLauncherArtifact();
        doc.Launcher = artifact;
        if (!string.IsNullOrWhiteSpace(_options.StackId))
        {
            doc.SelfStackId = _options.StackId;
            var self = doc.Registry.FirstOrDefault(e => string.Equals(e.StackId, _options.StackId, StringComparison.Ordinal));
            if (self is not null)
            {
                if (!string.IsNullOrWhiteSpace(artifact.Version))
                {
                    self.LauncherVersion = artifact.Version;
                }

                // The container is authoritative for its own branding: advertise /branding/* only when the
                // manager has actually pushed the image file (covers the fallback doc before any push).
                self.BackgroundUrl = BrandingFileExists("background") ? "/branding/background" : string.Empty;
                self.LogoUrl = BrandingFileExists("logo") ? "/branding/logo" : string.Empty;

                // Same for news: advertise /news only when the manager has pushed a news feed to this stack.
                self.NewsUrl = NewsFeedExists() ? "/news" : string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(doc.ManifestPublicKey))
        {
            doc.ManifestPublicKey = DerivePublicKey();
        }

        return doc;
    }

    /// <summary>Persists a manager-pushed registry snapshot (POST /portal).</summary>
    public async Task SavePortalAsync(StackPortalDocument document, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_options.CacheDir);
            var path = Path.Combine(_options.CacheDir, PortalFileName);
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
            _logger.LogInformation(
                "Persisted portal registry snapshot: {Count} stacks, revision {Revision}.",
                document.Registry.Count, document.RegistryRevision);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Latest launcher artifact info (from build.json in the launcher-dist volume).</summary>
    public LauncherArtifactInfo GetLauncherArtifact() => ReadLauncherArtifact();

    private string BrandingDir => Path.Combine(_options.CacheDir, "branding");

    private bool BrandingFileExists(string asset) => File.Exists(Path.Combine(BrandingDir, asset));

    private const string NewsFileName = "news.json";
    private string NewsDir => Path.Combine(_options.CacheDir, "news");

    private bool NewsFeedExists() => File.Exists(Path.Combine(NewsDir, NewsFileName));

    /// <summary>Returns the pushed launcher news feed JSON (served verbatim at <c>GET /news</c>), or null.</summary>
    public string? ReadNewsJson()
    {
        try
        {
            var path = Path.Combine(NewsDir, NewsFileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read pushed news.json.");
            return null;
        }
    }

    /// <summary>
    /// Resolves a news cover image the manager pushed into the news cache dir (stored extension-less as
    /// <c>{itemId}</c>), sniffing its content type. Null when the id is unsafe or no image was pushed.
    /// </summary>
    public (string Path, string ContentType)? ResolveNewsImageFile(string itemId)
    {
        if (!IsSafeNewsImageId(itemId))
        {
            return null;
        }

        var path = Path.Combine(NewsDir, itemId);
        return File.Exists(path) ? (path, SniffImageContentType(path)) : null;
    }

    // News item ids come from the manager (guids or "global-"+guid). Restrict to a safe set so the id can
    // never traverse out of the news dir.
    private static bool IsSafeNewsImageId(string itemId) =>
        !string.IsNullOrWhiteSpace(itemId)
        && itemId.Length <= 128
        && itemId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && !itemId.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Resolves a branding image (<c>background</c>/<c>logo</c>) the manager pushed into the cache volume,
    /// sniffing its content type from the magic bytes (the file is stored extension-less). Null when the
    /// asset name is invalid or no image has been pushed.
    /// </summary>
    public (string Path, string ContentType)? ResolveBrandingFile(string asset)
    {
        if (asset is not ("background" or "logo"))
        {
            return null;
        }

        var path = Path.Combine(BrandingDir, asset);
        return File.Exists(path) ? (path, SniffImageContentType(path)) : null;
    }

    private static string SniffImageContentType(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[12];
            using var fs = File.OpenRead(path);
            var read = fs.Read(head);
            if (read >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            {
                return "image/png";
            }
            if (read >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            {
                return "image/jpeg";
            }
            if (read >= 3 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46)
            {
                return "image/gif";
            }
            if (read >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
                && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
            {
                return "image/webp";
            }
            if (read >= 2 && head[0] == 0x42 && head[1] == 0x4D)
            {
                return "image/bmp";
            }
        }
        catch
        {
            // Fall through to a safe default.
        }

        return "image/png";
    }

    /// <summary>Resolves the absolute path of the launcher exe to download, or null when none is present.</summary>
    public string? ResolveLauncherFile()
    {
        var manifest = ReadBuildManifest();
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.FileName))
        {
            return null;
        }

        var path = Path.Combine(_options.LauncherDistDir, manifest.FileName);
        return File.Exists(path) ? path : null;
    }

    private StackPortalDocument? ReadPersisted()
    {
        try
        {
            var path = Path.Combine(_options.CacheDir, PortalFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StackPortalDocument>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read persisted portal.json; serving fallback.");
            return null;
        }
    }

    private StackPortalDocument BuildFallback()
    {
        var portalUrl = string.Empty; // Unknown from inside the container; the launcher already knows it.
        var display = string.IsNullOrWhiteSpace(_options.DisplayName) ? _options.AppName : _options.DisplayName;

        var self = new StackRegistryEntry
        {
            StackId = _options.StackId,
            DisplayName = display,
            PortalUrl = portalUrl,
            RealmlistHost = _options.RealmlistHost,
            RealmlistPort = _options.RealmlistPort,
            ArmoryPort = _options.ArmoryPort,
            Template = _options.Template,
            AccentColor = _options.AccentColor,
            SortOrder = 0,
            Revision = 0,
        };

        return new StackPortalDocument
        {
            SchemaVersion = 1,
            RegistryRevision = 0,
            GeneratedAt = DateTime.UtcNow,
            AppName = _options.AppName,
            BrandingTitle = display,
            AccentColor = _options.AccentColor,
            Template = _options.Template,
            RequireLogin = _options.RequireLogin,
            SelfStackId = _options.StackId,
            Registry = string.IsNullOrWhiteSpace(_options.StackId)
                ? new List<StackRegistryEntry>()
                : new List<StackRegistryEntry> { self },
        };
    }

    private LauncherArtifactInfo ReadLauncherArtifact()
    {
        var manifest = ReadBuildManifest();
        if (manifest is null)
        {
            return new LauncherArtifactInfo { DownloadAvailable = false };
        }

        var exePath = string.IsNullOrWhiteSpace(manifest.FileName)
            ? null
            : Path.Combine(_options.LauncherDistDir, manifest.FileName);
        var available = exePath is not null && File.Exists(exePath);

        return new LauncherArtifactInfo
        {
            Version = manifest.Version,
            BuiltAt = manifest.BuiltAt,
            SizeBytes = manifest.SizeBytes,
            Sha256 = manifest.Sha256,
            DownloadAvailable = available,
        };
    }

    private LauncherBuildManifest? ReadBuildManifest()
    {
        try
        {
            var path = Path.Combine(_options.LauncherDistDir, BuildManifestFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LauncherBuildManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read launcher build.json.");
            return null;
        }
    }

    private string DerivePublicKey()
    {
        if (string.IsNullOrWhiteSpace(_options.ManifestPrivateKey))
        {
            return string.Empty;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(_options.ManifestPrivateKey), out _);
            return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to derive manifest public key.");
            return string.Empty;
        }
    }
}
