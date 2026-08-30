using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Stores and serves a stack's operator-uploaded armory asset bundles under a persistent per-stack
/// location (<c>{ArmoryAssets:RootPath}/stacks/{stackId}</c>, on the data volume). The model-viewer
/// dataset feeds the stack's armory-assets volume; the static bundle is baked into the stack's armory
/// image on rebuild. Both take precedence over the assets baked into the manager image.
/// </summary>
public sealed class ArmoryAssetsService : IArmoryAssetsService
{
    // Expected top-level folders of the model-viewer dataset (armory.data.zip / armory.textures.zip),
    // used to detect the archive's real root (some bundles wrap everything in a single folder).
    private static readonly string[] DataMarkerFolders =
        ["bone", "dbc", "dbc_transmog", "meta", "mo3", "progression", "textures"];

    private const string StylingConfigFileName = "armory-styling.json";
    private const string LayoutConfigFileName = "armory-layout.json";
    private const string ThemeCssRelativePath = "css/azp-theme.css";
    private const string LayoutCssRelativePath = "css/azp-layout.css";
    private const string LayoutRuntimeRelativePath = "data/armory-layout.json";
    private const string WallpaperFilePrefix = "azp-wallpaper";
    private const string FaviconFilePrefix = "azp-favicon";
    private static readonly HashSet<string> WallpaperExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif"
    };
    private static readonly HashSet<string> FaviconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ico", ".png", ".svg", ".webp", ".gif"
    };

    private static readonly string[] ReleaseDataAssetNames = ["armory.data.zip", "armory.textures.zip"];
    private const string ReleaseStaticAssetName = "armory.static.zip";
    private static readonly JsonSerializerOptions GitHubJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ArmoryAssetsOptions _options;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly IArmoryImageService _armoryImageService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArmoryAssetsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArmoryAssetsService(
        IOptions<ArmoryAssetsOptions> options,
        IRemoteEngineService remoteEngine,
        IArmoryImageService armoryImageService,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<ArmoryAssetsService> logger)
    {
        _options = options.Value;
        _remoteEngine = remoteEngine;
        _armoryImageService = armoryImageService;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Dictionary<string, ArmoryStylingDto> GetStylingDefaults()
    {
        return Enum.GetValues<ArmoryStyleTemplate>()
            .ToDictionary(t => t.ToString(), ArmoryStylingTheme.DefaultFor);
    }

    public ArmoryPageLayoutDto GetPageTemplate(string pageId, string templateId)
    {
        return ArmoryLayoutDefaults.NormalizePage(
            ArmoryLayoutDefaults.PageTemplate(pageId, templateId), pageId);
    }

    public Task<ArmoryAssetsInfoDto> GetInfoAsync(string stackId, CancellationToken cancellationToken = default)
        => BuildInfoAsync(stackId, cancellationToken);

    public Task<ArmoryStylingDto> GetStylingAsync(string stackId, CancellationToken cancellationToken = default)
        => Task.FromResult(ReadStyling(stackId));

    public async Task<ArmoryStylingDto> SaveStylingAsync(
        string stackId, ArmoryStylingDto styling, CancellationToken cancellationToken = default)
    {
        var current = ReadStyling(stackId);
        var normalized = ArmoryStylingTheme.Normalize(styling);

        if (normalized.Template == ArmoryStyleTemplate.Custom)
        {
            normalized.WallpaperUrl = current.WallpaperUrl;
        }
        else
        {
            ClearCustomWallpaper(stackId);
            normalized.WallpaperUrl = null;
        }

        await WriteStylingAsync(stackId, normalized, cancellationToken);
        await WriteThemeCssAsync(stackId, normalized, cancellationToken);
        await TrySyncLiveArmoryShellAsync(stackId, cancellationToken);
        await MarkStaticRebuildPendingAsync(stackId, cancellationToken);

        return normalized;
    }

    public Task<ArmoryLayoutDto> GetLayoutAsync(string stackId, CancellationToken cancellationToken = default)
        => Task.FromResult(ReadLayout(stackId));

    public async Task<ArmoryLayoutDto> SaveLayoutAsync(
        string stackId, ArmoryLayoutDto layout, CancellationToken cancellationToken = default)
    {
        var normalized = ArmoryLayoutDefaults.Normalize(layout);
        await WriteLayoutAsync(stackId, normalized, cancellationToken);
        await WriteLayoutCssAsync(stackId, normalized, cancellationToken);
        await TrySyncLiveArmoryShellAsync(stackId, cancellationToken);
        await MarkStaticRebuildPendingAsync(stackId, cancellationToken);
        return normalized;
    }

    private async Task TrySyncLiveArmoryShellAsync(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            await _armoryImageService.SyncLiveLayoutAsync(stackId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync live armory shell into the running container for stack {StackId}.", stackId);
        }
    }

    public async Task<ArmoryStylingDto> UploadWallpaperAsync(
        string stackId, string fileName, Stream content, string? contentType = null, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ExtensionFromContentType(contentType);
        }

        if (string.IsNullOrWhiteSpace(extension) || !WallpaperExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Wallpaper must be an image (jpg, png, webp, gif, avif).");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var imgDir = Path.Combine(_options.StaticPathFor(stackId), "img");
            Directory.CreateDirectory(imgDir);

            foreach (var existing in Directory.EnumerateFiles(imgDir, $"{WallpaperFilePrefix}.*"))
            {
                File.Delete(existing);
            }

            var safeExtension = extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : extension.ToLowerInvariant();
            var wallpaperName = $"{WallpaperFilePrefix}{safeExtension}";
            var target = Path.Combine(imgDir, wallpaperName);
            await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            var styling = ReadStyling(stackId);
            styling.Template = ArmoryStyleTemplate.Custom;
            styling.AdvancedEnabled = true;
            styling.WallpaperUrl = $"img/{wallpaperName}";
            styling = ArmoryStylingTheme.Normalize(styling);
            await WriteStylingAsync(stackId, styling, cancellationToken);
            await WriteThemeCssAsync(stackId, styling, cancellationToken);
            await TrySyncLiveArmoryShellAsync(stackId, cancellationToken);
            await MarkStaticRebuildPendingAsync(stackId, cancellationToken);

            return styling;
        }
        finally
        {
            _gate.Release();
        }
    }

    public (string Path, string ContentType)? TryGetWallpaperFile(string stackId)
    {
        var imgDir = Path.Combine(_options.StaticPathFor(stackId), "img");
        if (!Directory.Exists(imgDir))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(imgDir, $"{WallpaperFilePrefix}.*"))
        {
            var extension = Path.GetExtension(path);
            var contentType = extension.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".avif" => "image/avif",
                _ => null,
            };
            if (contentType is null)
            {
                continue;
            }

            return (path, contentType);
        }

        return null;
    }

    public async Task<ArmoryAssetsInfoDto> UploadFaviconAsync(
        string stackId, string fileName, Stream content, string? contentType = null, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = FaviconExtensionFromContentType(contentType);
        }

        if (string.IsNullOrWhiteSpace(extension) || !FaviconExtensions.Contains(extension))
        {
            throw new InvalidOperationException("Favicon must be an image (ico, png, svg, webp, gif).");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var imgDir = Path.Combine(_options.StaticPathFor(stackId), "img");
            Directory.CreateDirectory(imgDir);

            foreach (var existing in Directory.EnumerateFiles(imgDir, $"{FaviconFilePrefix}.*"))
            {
                File.Delete(existing);
            }

            var safeExtension = extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : extension.ToLowerInvariant();
            var faviconName = $"{FaviconFilePrefix}{safeExtension}";
            var target = Path.Combine(imgDir, faviconName);
            await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            await TrySyncLiveArmoryShellAsync(stackId, cancellationToken);
            await MarkStaticRebuildPendingAsync(stackId, cancellationToken);

            return await BuildInfoAsync(stackId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public (string Path, string ContentType)? TryGetFaviconFile(string stackId)
    {
        var imgDir = Path.Combine(_options.StaticPathFor(stackId), "img");
        if (!Directory.Exists(imgDir))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(imgDir, $"{FaviconFilePrefix}.*"))
        {
            var extension = Path.GetExtension(path);
            var contentType = FaviconContentTypeForExtension(extension);
            if (contentType is null)
            {
                continue;
            }

            return (path, contentType);
        }

        return null;
    }

    public async Task<ArmoryAssetsInfoDto> DeleteFaviconAsync(string stackId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ClearCustomFavicon(stackId);
            await TrySyncLiveArmoryShellAsync(stackId, cancellationToken);
            await MarkStaticRebuildPendingAsync(stackId, cancellationToken);
            return await BuildInfoAsync(stackId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ArmoryAssetsInfoDto> UploadDataAsync(string stackId, Stream archiveStream, CancellationToken cancellationToken = default)
    {
        var tempDataPath = CreateUploadStagingDir(stackId);
        try
        {
            await ExtractIntoAsync(archiveStream, tempDataPath, DataMarkerFolders, cancellationToken);

            if (!Directory.Exists(tempDataPath) ||
                !Directory.EnumerateFileSystemEntries(tempDataPath).Any())
            {
                throw new InvalidOperationException(
                    "The uploaded archive did not contain any armory model-viewer data. " +
                    "Expected folders such as meta/, mo3/, bone/, textures/, progression/, or dbc/.");
            }

            if (!ContainsAnyDirectory(tempDataPath, DataMarkerFolders) &&
                !Directory.Exists(Path.Combine(tempDataPath, "progression")))
            {
                throw new InvalidOperationException(
                    "The uploaded archive did not contain a recognizable armory dataset. " +
                    "Expected top-level folders such as meta/, mo3/, bone/, or textures/ " +
                    "(or the same folders under a single data/ wrapper).");
            }

            await RefreshAssetsVolumeAsync(stackId, tempDataPath, cancellationToken);
        }
        finally
        {
            TryDeleteDir(tempDataPath);
        }

        var info = await BuildInfoAsync(stackId, cancellationToken);
        if (!info.DataOnStackVolume && info.DataFileCount == 0)
        {
            throw new InvalidOperationException(
                "Armory data was extracted but could not be verified on the stack's armory-assets volume. " +
                "Check that the stack's Docker engine is reachable, then try again.");
        }

        return info;
    }

    /// <summary>
    /// Pushes the on-disk dataset into the stack's armory-assets Docker volume and makes it world-readable.
    /// Only top-level files/folders present in <paramref name="dataPath"/> are replaced on the volume so
    /// separate <c>armory.data.zip</c> and <c>armory.textures.zip</c> uploads merge instead of wiping each other.
    /// </summary>
    private async Task RefreshAssetsVolumeAsync(
        string stackId,
        string dataPath,
        CancellationToken cancellationToken)
    {
        var volumeName = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stackId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await db.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (stack is null)
        {
            throw new InvalidOperationException(
                $"Stack '{stackId}' was not found; cannot publish armory data to its Docker volume.");
        }

        await _remoteEngine.EnsureVolumeExistsAsync(stack, volumeName, cancellationToken);
        await ReplaceStagedVolumePathsAsync(stack, volumeName, dataPath, cancellationToken);
        await _remoteEngine.SeedVolumeAsync(stack, volumeName, dataPath, cancellationToken);
        await _remoteEngine.SetVolumeWorldReadableAsync(stack, volumeName, cancellationToken);

        if (!await HasArmoryDatasetOnVolumeAsync(stack, volumeName, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Armory data was copied to volume '{volumeName}' but expected folders " +
                $"(meta/, mo3/, bone/, etc.) were not found at the volume root.");
        }

        _logger.LogInformation(
            "Seeded armory assets volume {Volume} for stack {StackId} from {Source}.",
            volumeName, stackId, dataPath);
    }

    /// <summary>
    /// Removes from the volume only the top-level entries that exist in the staged upload directory,
    /// so a textures-only or data-only zip does not delete unrelated folders already on the volume.
    /// </summary>
    private async Task ReplaceStagedVolumePathsAsync(
        ManagedStackEntity stack,
        string volumeName,
        string dataPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dataPath))
        {
            return;
        }

        var pathsToReplace = Directory.EnumerateFileSystemEntries(dataPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (pathsToReplace.Count == 0)
        {
            return;
        }

        await _remoteEngine.DeleteVolumePathsAsync(stack, volumeName, pathsToReplace, cancellationToken);
    }

    /// <summary>
    /// Pushes uploaded static web assets into the stack's armory-static Docker volume.
    /// </summary>
    private async Task RefreshStaticVolumeAsync(
        string stackId,
        string staticPath,
        bool replaceContents,
        CancellationToken cancellationToken)
    {
        var volumeName = DockerComposeOverrideGenerator.ArmoryStaticVolumeName(stackId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await db.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        if (replaceContents)
        {
            await _remoteEngine.EnsureVolumeExistsAsync(stack, volumeName, cancellationToken);
            await _remoteEngine.ClearVolumeContentsAsync(stack, volumeName, cancellationToken);
        }

        if (stack is null)
        {
            throw new InvalidOperationException(
                $"Stack '{stackId}' was not found; cannot publish armory static assets to its Docker volume.");
        }

        await _remoteEngine.SeedVolumeAsync(stack, volumeName, staticPath, cancellationToken);
    }

    public async Task<ArmoryAssetsInfoDto> UploadStaticAsync(string stackId, Stream archiveStream, CancellationToken cancellationToken = default)
    {
        var tempStaticPath = CreateUploadStagingDir(stackId);
        try
        {
            await ExtractIntoAsync(archiveStream, tempStaticPath, markerFolders: null, cancellationToken);

            if (!Directory.Exists(tempStaticPath) ||
                !Directory.EnumerateFileSystemEntries(tempStaticPath).Any())
            {
                throw new InvalidOperationException("The uploaded archive did not contain any static web assets.");
            }

            await RefreshStaticVolumeAsync(stackId, tempStaticPath, replaceContents: true, cancellationToken);
        }
        finally
        {
            TryDeleteDir(tempStaticPath);
        }

        await MarkStaticRebuildPendingAsync(stackId, cancellationToken);

        var info = await BuildInfoAsync(stackId, cancellationToken);
        if (!info.StaticUploaded || info.StaticFileCount == 0)
        {
            throw new InvalidOperationException(
                "Static assets were extracted but could not be verified on the stack's armory-static volume. " +
                "Check that the stack's Docker engine is reachable, then try again.");
        }

        return info;
    }

    public async Task<ArmoryReleaseDownloadResultDto> DownloadLatestReleaseAssetsAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var repository = _options.ReleaseRepository?.Trim();
        var releaseTag = _options.ReleaseTag?.Trim();
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(releaseTag))
        {
            throw new InvalidOperationException(
                "Armory release download is not configured (ArmoryAssets:ReleaseRepository and ReleaseTag).");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation(
                "Downloading armory release assets from {Repository} tag {Tag} for stack {StackId}",
                repository,
                releaseTag,
                stackId);

            var release = await FetchGitHubReleaseByTagAsync(repository, releaseTag, cancellationToken);
            var downloaded = new List<string>();
            var missing = new List<string>();

            foreach (var assetName in ReleaseDataAssetNames)
            {
                var asset = release.Assets.FirstOrDefault(a =>
                    string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
                if (asset is null)
                {
                    missing.Add(assetName);
                    continue;
                }

                await DownloadAndApplyDataAssetAsync(stackId, asset, cancellationToken);
                downloaded.Add(assetName);
            }

            var staticAsset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, ReleaseStaticAssetName, StringComparison.OrdinalIgnoreCase));
            if (staticAsset is null)
            {
                missing.Add(ReleaseStaticAssetName);
            }
            else
            {
                await DownloadAndApplyStaticAssetAsync(stackId, staticAsset, cancellationToken);
                downloaded.Add(ReleaseStaticAssetName);
            }

            if (downloaded.Count == 0)
            {
                throw new InvalidOperationException(
                    $"GitHub release '{releaseTag}' on {repository} did not contain any of: " +
                    $"{string.Join(", ", ReleaseDataAssetNames.Concat([ReleaseStaticAssetName]))}.");
            }

            var info = await BuildInfoAsync(stackId, cancellationToken);
            return new ArmoryReleaseDownloadResultDto
            {
                Info = info,
                ReleaseTag = releaseTag,
                DownloadedAssets = downloaded,
                MissingAssets = missing,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAndApplyDataAssetAsync(
        string stackId,
        GitHubReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        using var scratch = TempWorkspace.CreateFile("armory-data", Path.GetExtension(asset.Name));
        _logger.LogInformation("Downloading {Asset} ({Size} bytes) for stack {StackId}", asset.Name, asset.Size, stackId);
        await DownloadReleaseAssetToFileAsync(asset.BrowserDownloadUrl, scratch.Path, cancellationToken);
        await using var stream = File.OpenRead(scratch.Path);
        await UploadDataAsync(stackId, stream, cancellationToken);
    }

    private async Task DownloadAndApplyStaticAssetAsync(
        string stackId,
        GitHubReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        using var scratch = TempWorkspace.CreateFile("armory-static", Path.GetExtension(asset.Name));
        _logger.LogInformation("Downloading {Asset} ({Size} bytes) for stack {StackId}", asset.Name, asset.Size, stackId);
        await DownloadReleaseAssetToFileAsync(asset.BrowserDownloadUrl, scratch.Path, cancellationToken);
        await using var stream = File.OpenRead(scratch.Path);
        await UploadStaticAsync(stackId, stream, cancellationToken);
    }

    private async Task<GitHubReleaseResponse> FetchGitHubReleaseByTagAsync(
        string repository,
        string tag,
        CancellationToken cancellationToken)
    {
        using var api = CreateGitHubApiClient();
        var url = $"repos/{repository}/releases/tags/{Uri.EscapeDataString(tag)}";
        using var response = await api.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"GitHub release '{tag}' was not found on {repository} (HTTP {(int)response.StatusCode}). {body}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var release = JsonSerializer.Deserialize<GitHubReleaseResponse>(content, GitHubJsonOptions);
        if (release is null || release.Assets.Count == 0)
        {
            throw new InvalidOperationException(
                $"GitHub release '{tag}' on {repository} has no downloadable assets.");
        }

        return release;
    }

    private async Task DownloadReleaseAssetToFileAsync(
        string downloadUrl,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromHours(3);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AzerothPlatform", "1.0"));

        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private HttpClient CreateGitHubApiClient()
    {
        var client = _httpClientFactory.CreateClient("GitHubApi");
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AzerothPlatform", "1.0"));
        }

        return client;
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }

    public async Task<ArmoryAssetsInfoDto> DeleteStaticAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var deletedAny = false;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            var stack = await db.ManagedStacks
                .AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

            var staticVolume = DockerComposeOverrideGenerator.ArmoryStaticVolumeName(stackId);
            var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, staticVolume, cancellationToken);
            if (summary.VolumeExists && summary.FileCount > 0)
            {
                await _remoteEngine.ClearVolumeContentsAsync(stack, staticVolume, cancellationToken);
                deletedAny = true;
                _logger.LogInformation("Cleared armory static volume for stack {StackId}.", stackId);
            }
        }

        var staticRoot = _options.StaticPathFor(stackId);
        if (Directory.Exists(staticRoot))
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                foreach (var file in Directory.EnumerateFiles(staticRoot))
                {
                    File.Delete(file);
                    deletedAny = true;
                }

                foreach (var dir in Directory.EnumerateDirectories(staticRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (string.Equals(name, _options.DataDirName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(name, "css", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "img", StringComparison.OrdinalIgnoreCase))
                    {
                        deletedAny |= DeleteDirectoryContentsExcept(dir, IsGeneratedStylingAsset);
                        DeleteIfEmpty(dir);
                        continue;
                    }

                    Directory.Delete(dir, recursive: true);
                    deletedAny = true;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        if (deletedAny)
        {
            await MarkStaticRebuildPendingAsync(stackId, cancellationToken);
        }

        return await BuildInfoAsync(stackId, cancellationToken);
    }

    public Task ClearStaticRebuildPendingAsync(string stackId, CancellationToken cancellationToken = default)
    {
        try
        {
            var marker = _options.RebuildMarkerPath(stackId);
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clear the armory static rebuild marker for stack {StackId}.", stackId);
        }

        return Task.CompletedTask;
    }

    public async Task<ClientBrowseResultDto> BrowseDataAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRelative(relativePath);
        var result = new ClientBrowseResultDto { Path = normalized };

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await db.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        var volumeName = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stackId);
        var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);
        if (summary.VolumeExists && (summary.FileCount > 0 || normalized.Length == 0))
        {
            IReadOnlyList<VolumeDirectoryEntry> volumeEntries;
            try
            {
                volumeEntries = await _remoteEngine.ListVolumeDirectoryAsync(stack, volumeName, normalized, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                _logger.LogDebug(ex, "Rejected unsafe armory dataset browse path '{Path}' for stack {StackId}.", normalized, stackId);
                return result;
            }

            if (normalized.Length == 0 || volumeEntries.Count > 0)
            {
                result.Exists = normalized.Length == 0 ? summary.FileCount > 0 : true;
                foreach (var entry in volumeEntries)
                {
                    if (entry.Name is ".DS_Store")
                    {
                        continue;
                    }

                    result.Entries.Add(new ClientBrowseEntryDto
                    {
                        Name = entry.Name,
                        IsDirectory = entry.IsDirectory,
                        Size = entry.IsDirectory ? 0 : entry.SizeBytes,
                        ItemCount = entry.IsDirectory ? entry.ItemCount : 0,
                        RelativePath = CombineRelative(normalized, entry.Name),
                    });
                }

                result.Entries.Sort((a, b) =>
                    a.IsDirectory != b.IsDirectory
                        ? (a.IsDirectory ? -1 : 1)
                        : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                return result;
            }
        }

        return BrowseDataFromManager(stackId, normalized);
    }

    private ClientBrowseResultDto BrowseDataFromManager(string stackId, string normalized)
    {
        var dataRoot = _options.DataPathFor(stackId);
        var result = new ClientBrowseResultDto { Path = normalized };

        var target = ResolveWithin(dataRoot, normalized);
        if (target is null || !Directory.Exists(target))
        {
            return result;
        }

        result.Exists = true;

        foreach (var dir in Directory.EnumerateDirectories(target))
        {
            var name = Path.GetFileName(dir);
            var childCount = 0;
            try
            {
                childCount = Directory.EnumerateFileSystemEntries(dir).Count();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to count children of {Dir}", dir);
            }

            result.Entries.Add(new ClientBrowseEntryDto
            {
                Name = name,
                IsDirectory = true,
                ItemCount = childCount,
                RelativePath = CombineRelative(normalized, name),
            });
        }

        foreach (var file in Directory.EnumerateFiles(target))
        {
            var name = Path.GetFileName(file);
            if (name == ".DS_Store")
            {
                continue;
            }

            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to stat {File}", file);
            }

            result.Entries.Add(new ClientBrowseEntryDto
            {
                Name = name,
                IsDirectory = false,
                Size = size,
                RelativePath = CombineRelative(normalized, name),
            });
        }

        result.Entries.Sort((a, b) =>
            a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return result;
    }

    public async Task<ArmoryAssetsInfoDto> DeleteDataAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRelative(relativePath);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Refusing to delete the dataset root. Delete individual files or folders instead.");
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await db.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        var volumeName = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stackId);
        var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (summary.VolumeExists && summary.FileCount > 0)
            {
                if (stack is null)
                {
                    await _remoteEngine.DeleteLocalVolumePathsAsync(volumeName, [normalized], cancellationToken);
                }
                else
                {
                    await _remoteEngine.DeleteVolumePathsAsync(stack, volumeName, [normalized], cancellationToken);
                }

                _logger.LogInformation("Deleted '{Path}' from armory assets volume for stack {StackId}.", normalized, stackId);
            }
            else
            {
                var dataRoot = _options.DataPathFor(stackId);
                var target = ResolveWithin(dataRoot, normalized)
                    ?? throw new InvalidOperationException("Invalid path.");

                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
                else
                {
                    throw new InvalidOperationException("The file or folder no longer exists.");
                }

                _logger.LogInformation("Deleted '{Path}' from armory dataset for stack {StackId}.", normalized, stackId);

                WriteProgressionManifest(dataRoot);
                MakeWorldReadable(dataRoot);
            }
        }
        finally
        {
            _gate.Release();
        }

        return await BuildInfoAsync(stackId, cancellationToken);
    }

    public async Task<ArmoryAssetsInfoDto> UploadDataFileAsync(
        string stackId, string relativeDir, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var safeName = SanitizeFileName(fileName);
        var normalizedDir = NormalizeRelative(relativeDir);
        var relativeFile = CombineRelative(normalizedDir, safeName);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await db.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);

        var volumeName = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stackId);
        var stagingDir = CreateUploadStagingDir(stackId);
        var targetFile = Path.Combine(stagingDir, relativeFile.Replace('/', Path.DirectorySeparatorChar));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await using (var file = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            WriteProgressionManifest(stagingDir);
            MakeWorldReadable(stagingDir);

            if (stack is null)
            {
                throw new InvalidOperationException(
                    $"Stack '{stackId}' was not found; cannot upload into its armory assets volume.");
            }

            await _remoteEngine.SeedVolumeAsync(stack, volumeName, stagingDir, cancellationToken);
            await _remoteEngine.SetVolumeWorldReadableAsync(stack, volumeName, cancellationToken);

            _logger.LogInformation("Uploaded '{Path}' into armory assets volume for stack {StackId}.", relativeFile, stackId);
        }
        finally
        {
            TryDeleteDir(stagingDir);
            _gate.Release();
        }

        return await BuildInfoAsync(stackId, cancellationToken);
    }

    /// <summary>Strips any directory components and rejects empty names so an upload can't escape its folder.</summary>
    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            throw new InvalidOperationException("A valid file name is required.");
        }
        return name;
    }

    private static string NormalizeRelative(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/').Trim('/');

    /// <summary>
    /// Resolves <paramref name="normalizedRelative"/> against <paramref name="root"/>, returning the
    /// absolute path only when it stays within the root (defends against <c>..</c> traversal).
    /// </summary>
    private static string? ResolveWithin(string root, string normalizedRelative)
    {
        var basePath = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(basePath, normalizedRelative));
        if (combined != basePath &&
            !combined.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return combined;
    }

    private static string CombineRelative(string parent, string name)
        => string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";

    /// <summary>
    /// Streams the upload to a staging file, extracts it, resolves the archive's real root, then merges
    /// the contents into <paramref name="targetDir"/> (overwriting matching files, keeping others).
    /// </summary>
    private async Task ExtractIntoAsync(
        Stream archiveStream, string targetDir, string[]? markerFolders, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        Directory.CreateDirectory(_options.RootPath);
        var stagingDir = Path.Combine(_options.RootPath, $".upload-{Guid.NewGuid():N}");
        var tempArchive = Path.Combine(stagingDir, "upload.archive");
        var tempExtract = Path.Combine(stagingDir, "extract");
        try
        {
            Directory.CreateDirectory(stagingDir);

            await using (var file = new FileStream(
                tempArchive, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await archiveStream.CopyToAsync(file, cancellationToken);
            }

            Directory.CreateDirectory(tempExtract);
            _logger.LogInformation("Extracting uploaded armory asset archive to {Dir}...", tempExtract);
            ExtractArchive(tempArchive, tempExtract, cancellationToken);

            var root = ResolveExtractRoot(tempExtract, markerFolders);

            Directory.CreateDirectory(targetDir);
            if (ReferenceEquals(markerFolders, DataMarkerFolders) && LooksLikeProgressionContentRoot(root))
            {
                // Support zips made from inside the progression folder, where dungeon/raid/world sit
                // at archive root instead of under a top-level progression/ directory.
                MergeCopy(root, Path.Combine(targetDir, "progression"), cancellationToken);
            }
            else
            {
                MergeCopy(root, targetDir, cancellationToken);
            }

            WriteProgressionManifest(targetDir);

            // Extracted trees can carry restrictive source permissions; the armory-assets nginx worker
            // (non-root) and the image build must be able to read every file.
            MakeWorldReadable(targetDir);

            _logger.LogInformation("Armory assets merged into {Target}.", targetDir);
        }
        finally
        {
            TryDeleteDir(stagingDir);
            _gate.Release();
        }
    }

    /// <summary>
    /// Finds the archive's real content root. If the extraction root already contains the expected
    /// dataset folders it is used directly; otherwise a single wrapping folder is descended into.
    /// </summary>
    private static string ResolveExtractRoot(string extractRoot, string[]? markerFolders)
    {
        if (markerFolders is not null && ContainsAnyDirectory(extractRoot, markerFolders))
        {
            return extractRoot;
        }

        if (markerFolders is not null)
        {
            foreach (var wrapper in new[] { "data", Path.Combine("static", "data") })
            {
                var wrapped = Path.Combine(extractRoot, wrapper);
                if (Directory.Exists(wrapped) && ContainsAnyDirectory(wrapped, markerFolders))
                {
                    return wrapped;
                }
            }
        }

        // Ignore macOS cruft and hidden dot-entries so a lone real wrapping folder is still detected even
        // if such junk sits alongside it at the archive root.
        var dirs = Directory.GetDirectories(extractRoot)
            .Where(d => !IsMacOsCruft(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.'))
            .ToArray();
        var files = Directory.GetFiles(extractRoot)
            .Where(f => !IsMacOsCruft(Path.GetFileName(f)) && !Path.GetFileName(f).StartsWith('.'))
            .ToArray();
        if (files.Length == 0 && dirs.Length == 1)
        {
            var inner = dirs[0];
            if (markerFolders is null || ContainsAnyDirectory(inner, markerFolders) || Directory.GetDirectories(inner).Length > 0)
            {
                return inner;
            }
        }

        return extractRoot;
    }

    private static bool ContainsAnyDirectory(string root, IEnumerable<string> names)
        => names.Any(name => Directory.Exists(Path.Combine(root, name)));

    private static bool LooksLikeProgressionContentRoot(string root)
        => (Directory.Exists(Path.Combine(root, "dungeon")) ||
            Directory.Exists(Path.Combine(root, "raid")) ||
            Directory.Exists(Path.Combine(root, "world"))) &&
           !Directory.Exists(Path.Combine(root, "progression"));

    /// <summary>Recursively copies <paramref name="source"/> into <paramref name="destination"/>, overwriting files.</summary>
    private static void MergeCopy(string source, string destination, CancellationToken cancellationToken)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (name == ".DS_Store")
            {
                continue;
            }
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void MakeWorldReadable(string root)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
            {
                File.SetUnixFileMode(dir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(file,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }
        catch
        {
            // Best-effort; the volume seed also applies a world-readable pass per stack.
        }
    }

    /// <summary>
    /// Writes <c>progression/.images.json</c>, a lookup table the armory uses to resolve instance card
    /// artwork from the uploaded asset sidecar (nginx does not expose directory listings). Keys are
    /// <c>{content}/{expansion}/{normalizedBasename}</c>; values are asset-relative paths such as
    /// <c>progression/raid/classic/molten_core.png</c>.
    /// </summary>
    private static void WriteProgressionManifest(string dataRoot)
    {
        var progDir = Path.Combine(dataRoot, "progression");
        if (!Directory.Exists(progDir))
        {
            return;
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(progDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name is ".images.json" or ".DS_Store" || name.StartsWith("._", StringComparison.Ordinal))
            {
                continue;
            }

            var ext = Path.GetExtension(file);
            if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rel = Path.GetRelativePath(progDir, file).Replace('\\', '/');
            var parts = rel.Split('/');
            if (parts.Length < 3)
            {
                continue;
            }

            var normalized = NormalizeProgressionImageKey(Path.GetFileNameWithoutExtension(parts[^1]));
            var key = $"{parts[0]}/{parts[1]}/{normalized}";
            files[key] = $"progression/{rel}";
        }

        var manifestPath = Path.Combine(progDir, ".images.json");
        var json = JsonSerializer.Serialize(new { files }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json);
    }

    private static string NormalizeProgressionImageKey(string value)
    {
        var lower = value.ToLowerInvariant();
        var chars = new char[lower.Length];
        var length = 0;
        foreach (var c in lower)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                chars[length++] = c;
            }
        }

        return length == lower.Length ? lower : new string(chars, 0, length);
    }

    private static void ExtractArchive(string archivePath, string destination, CancellationToken cancellationToken)
    {
        try
        {
            // 7z is a random-access-only format (no forward-only reader), so open it as an archive.
            // Everything else (zip, rar, tar, and - crucially - compressed tarballs .tar.gz/.tar.bz2/
            // .tar.xz) goes through ReaderFactory, which transparently unwraps the outer compression and
            // then reads the inner tar. ArchiveFactory would instead treat a .tar.gz as a single-entry
            // gzip and never expose the dataset, so this is required for those formats to work at all.
            if (IsSevenZip(archivePath))
            {
                using var archive = ArchiveFactory.OpenArchive(new FileInfo(archivePath));
                using var reader = archive.ExtractAllEntries();
                ExtractEntries(reader, destination, cancellationToken);
            }
            else
            {
                using var stream = File.OpenRead(archivePath);
                using var reader = ReaderFactory.OpenReader(stream);
                ExtractEntries(reader, destination, cancellationToken);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Surface the underlying reason (e.g. "unknown archive type", "corrupt", password-protected)
            // so the operator can tell what's actually wrong instead of a generic failure.
            throw new InvalidOperationException(
                $"The uploaded file could not be extracted ({ex.Message}). Supported formats are zip, rar, 7z, and tar (optionally gzip/bzip2/xz compressed).",
                ex);
        }
    }

    /// <summary>Writes every non-directory entry of <paramref name="reader"/> into the destination, guarding against zip-slip.</summary>
    private static void ExtractEntries(IReader reader, string destination, CancellationToken cancellationToken)
    {
        var options = new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true,
            PreserveFileTime = false,
        };
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory)
            {
                continue;
            }
            // Drop macOS archive cruft (the __MACOSX metadata tree and AppleDouble/.DS_Store sidecars).
            // Besides being useless, a top-level __MACOSX/ folder sitting next to the real content makes
            // ResolveExtractRoot see two top-level entries, so it fails to strip the single wrapping
            // folder and leaves everything double-nested (e.g. static/static/index.hbs).
            if (IsMacOsCruft(reader.Entry.Key))
            {
                continue;
            }
            // Zip-slip guard: reject any entry whose resolved path escapes the destination.
            EnsureEntryWithinDestination(destination, reader.Entry.Key);
            reader.WriteEntryToDirectory(destination, options);
        }
    }

    /// <summary>
    /// True for macOS archive cruft: the <c>__MACOSX</c> resource-fork tree, <c>.DS_Store</c> files, and
    /// AppleDouble sidecars (<c>._name</c>). These are dropped on extraction so they neither pollute the
    /// asset tree nor defeat the single-wrapping-folder detection in <see cref="ResolveExtractRoot"/>.
    /// </summary>
    private static bool IsMacOsCruft(string? entryKey)
    {
        if (string.IsNullOrEmpty(entryKey))
        {
            return false;
        }
        var parts = entryKey.Split('/', '\\');
        return parts.Any(p => p is "__MACOSX" or ".DS_Store" || p.StartsWith("._", StringComparison.Ordinal));
    }

    /// <summary>Detects a 7z archive by its 6-byte magic signature (37 7A BC AF 27 1C).</summary>
    private static bool IsSevenZip(string archivePath)
    {
        try
        {
            Span<byte> sig = stackalloc byte[6];
            using var fs = File.OpenRead(archivePath);
            return fs.Read(sig) == 6
                && sig[0] == 0x37 && sig[1] == 0x7A && sig[2] == 0xBC
                && sig[3] == 0xAF && sig[4] == 0x27 && sig[5] == 0x1C;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Zip-slip guard: throws if an archive entry's resolved path would land outside
    /// <paramref name="destination"/> (e.g. a <c>../../</c> or absolute/rooted entry key).
    /// </summary>
    private static void EnsureEntryWithinDestination(string destination, string? entryKey)
    {
        var key = (entryKey ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (key.Length == 0)
        {
            throw new InvalidOperationException("The archive contains an entry with an empty path.");
        }

        var destFull = Path.GetFullPath(destination);
        var destWithSep = destFull.EndsWith(Path.DirectorySeparatorChar)
            ? destFull
            : destFull + Path.DirectorySeparatorChar;

        var target = Path.GetFullPath(Path.Combine(destFull, key));
        if (target != destFull && !target.StartsWith(destWithSep, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Archive entry escapes the extraction directory: {entryKey}");
        }
    }

    private async Task<ArmoryAssetsInfoDto> BuildInfoAsync(string stackId, CancellationToken cancellationToken)
    {
        var info = BuildInfo(stackId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await db.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return info;
        }

        var assetsVolume = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stackId);
        if (await _remoteEngine.VolumeExistsAsync(stack, assetsVolume, cancellationToken))
        {
            var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, assetsVolume, cancellationToken);
            if (summary.FileCount > 0)
            {
                info.DataFileCount = summary.FileCount;
                info.DataSize = summary.TotalBytes;
            }

            var folders = new List<string>();
            foreach (var folder in DataMarkerFolders)
            {
                if (await _remoteEngine.VolumeSubdirExistsAsync(stack, assetsVolume, folder, cancellationToken))
                {
                    folders.Add(folder);
                }
            }

            info.DataFolders = folders;
            info.DataUploaded = folders.Any(f => f.Equals("meta", StringComparison.OrdinalIgnoreCase))
                || folders.Any(f => f.Equals("progression", StringComparison.OrdinalIgnoreCase));
            info.DataOnStackVolume = folders.Count > 0 || info.DataFileCount > 0;

            if (info.DataOnStackVolume && info.DataFileCount == 0)
            {
                info.DataFileCount = await _remoteEngine.CountVolumeFilesAsync(
                    stack, assetsVolume, string.Empty, "*", cancellationToken);
            }

            if (info.DataOnStackVolume && info.DataSize == 0 && summary.TotalBytes > 0)
            {
                info.DataSize = summary.TotalBytes;
            }
        }

        var staticVolume = DockerComposeOverrideGenerator.ArmoryStaticVolumeName(stackId);
        if (await _remoteEngine.VolumeExistsAsync(stack, staticVolume, cancellationToken))
        {
            var staticSummary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, staticVolume, cancellationToken);
            if (staticSummary.FileCount > 0)
            {
                info.StaticFileCount = staticSummary.FileCount;
                info.StaticSize = staticSummary.TotalBytes;
                info.StaticUploaded = true;
                info.StaticOnStackVolume = true;
            }
        }

        var clientDataVolume = $"{DockerComposeOverrideGenerator.GetComposeProjectName(stackId)}_ac-client-data";
        info.ServerDbcFileCount = await _remoteEngine.CountVolumeFilesAsync(
            stack, clientDataVolume, "dbc", "*.dbc", cancellationToken);

        return info;
    }

    private ArmoryAssetsInfoDto BuildInfo(string stackId)
    {
        return new ArmoryAssetsInfoDto
        {
            StaticRebuildPending = File.Exists(_options.RebuildMarkerPath(stackId)),
            FaviconUploaded = TryGetFaviconFile(stackId) is not null,
        };
    }

    private static bool DeleteDirectoryContentsExcept(string root, Func<string, bool> keepRelativePath)
    {
        var deletedAny = false;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizePath(Path.GetRelativePath(root, file));
            if (keepRelativePath(relative))
            {
                continue;
            }

            File.Delete(file);
            deletedAny = true;
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            DeleteIfEmpty(dir);
        }

        return deletedAny;
    }

    private static void DeleteIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static (long Size, int Count) MeasureTree(string root)
    {
        long total = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name == ".DS_Store")
            {
                continue;
            }
            count++;
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // Ignore files that vanish mid-scan.
            }
        }

        return (total, count);
    }

    private static (long Size, int Count) MeasureStaticWebAssets(string staticRoot)
    {
        long total = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(staticRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name == ".DS_Store")
            {
                continue;
            }

            var relative = NormalizePath(Path.GetRelativePath(staticRoot, file));
            if (relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || IsGeneratedStylingAsset(relative))
            {
                continue;
            }

            count++;
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // Ignore files that vanish mid-scan.
            }
        }

        return (total, count);
    }

    private static bool IsGeneratedStylingAsset(string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        return normalized.Equals(ThemeCssRelativePath, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(LayoutCssRelativePath, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(LayoutRuntimeRelativePath, StringComparison.OrdinalIgnoreCase)
            || (normalized.StartsWith("img/", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(normalized).StartsWith(WallpaperFilePrefix + ".", StringComparison.OrdinalIgnoreCase))
            || (normalized.StartsWith("img/", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(normalized).StartsWith(FaviconFilePrefix + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string? ExtensionFromContentType(string? contentType) => contentType?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/avif" => ".avif",
        _ => null,
    };

    private void ClearCustomWallpaper(string stackId)
    {
        var imgDir = Path.Combine(_options.StaticPathFor(stackId), "img");
        if (!Directory.Exists(imgDir))
        {
            return;
        }

        foreach (var existing in Directory.EnumerateFiles(imgDir, $"{WallpaperFilePrefix}.*"))
        {
            File.Delete(existing);
        }
    }

    private void ClearCustomFavicon(string stackId)
    {
        var imgDir = Path.Combine(_options.StaticPathFor(stackId), "img");
        if (!Directory.Exists(imgDir))
        {
            return;
        }

        foreach (var existing in Directory.EnumerateFiles(imgDir, $"{FaviconFilePrefix}.*"))
        {
            File.Delete(existing);
        }
    }

    private static string? FaviconExtensionFromContentType(string? contentType) => contentType?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
    {
        "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
        "image/png" => ".png",
        "image/svg+xml" => ".svg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => null,
    };

    private static string? FaviconContentTypeForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".ico" => "image/x-icon",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => null,
    };

    private ArmoryStylingDto ReadStyling(string stackId)
    {
        var path = StylingConfigPath(stackId);
        if (!File.Exists(path))
        {
            return ArmoryStylingTheme.DefaultFor(ArmoryStyleTemplate.Classic);
        }

        try
        {
            var styling = JsonSerializer.Deserialize<ArmoryStylingDto>(File.ReadAllText(path));
            return ArmoryStylingTheme.Normalize(styling ?? ArmoryStylingTheme.DefaultFor(ArmoryStyleTemplate.Classic));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read armory styling for stack {StackId}; using Classic defaults.", stackId);
            return ArmoryStylingTheme.DefaultFor(ArmoryStyleTemplate.Classic);
        }
    }

    private async Task WriteStylingAsync(string stackId, ArmoryStylingDto styling, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.StackRootPath(stackId));
        var json = JsonSerializer.Serialize(styling, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(StylingConfigPath(stackId), json, cancellationToken);
    }

    private async Task WriteThemeCssAsync(string stackId, ArmoryStylingDto styling, CancellationToken cancellationToken)
    {
        var cssPath = Path.Combine(_options.StaticPathFor(stackId), ThemeCssRelativePath);
        var css = ArmoryStylingTheme.BuildCss(styling);

        if (string.IsNullOrWhiteSpace(css))
        {
            if (File.Exists(cssPath))
            {
                File.Delete(cssPath);
            }
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cssPath)!);
        await File.WriteAllTextAsync(cssPath, css, cancellationToken);
    }

    private ArmoryLayoutDto ReadLayout(string stackId)
    {
        var path = LayoutConfigPath(stackId);
        if (!File.Exists(path))
        {
            return ArmoryLayoutDefaults.Default();
        }

        try
        {
            var layout = ArmoryLayoutSerialization.FromRuntimeJson(File.ReadAllText(path));
            return ArmoryLayoutDefaults.Normalize(layout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read armory layout for stack {StackId}; using default.", stackId);
            return ArmoryLayoutDefaults.Default();
        }
    }

    private async Task WriteLayoutAsync(string stackId, ArmoryLayoutDto layout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.StackRootPath(stackId));
        var json = ArmoryLayoutSerialization.ToRuntimeJson(layout);
        await File.WriteAllTextAsync(LayoutConfigPath(stackId), json, cancellationToken);

        var runtimePath = Path.Combine(_options.StaticPathFor(stackId), LayoutRuntimeRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
        await File.WriteAllTextAsync(runtimePath, json, cancellationToken);
    }

    private async Task WriteLayoutCssAsync(string stackId, ArmoryLayoutDto layout, CancellationToken cancellationToken)
    {
        var staticPath = _options.StaticPathFor(stackId);
        var cssPath = Path.Combine(staticPath, LayoutCssRelativePath);
        var css = ArmoryLayoutTheme.BuildCss(layout);

        if (string.IsNullOrWhiteSpace(css))
        {
            if (File.Exists(cssPath))
            {
                File.Delete(cssPath);
            }
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cssPath)!);
        await File.WriteAllTextAsync(cssPath, css, cancellationToken);
        ArmoryLayoutTheme.EnsureLayoutStylesheetLinked(staticPath);
    }

    private async Task MarkStaticRebuildPendingAsync(string stackId, CancellationToken cancellationToken)
    {
        // Static assets and generated theme CSS are baked into the stack's armory image, so flag that a
        // rebuild is required for changes to take effect.
        try
        {
            Directory.CreateDirectory(_options.StackRootPath(stackId));
            await File.WriteAllTextAsync(_options.RebuildMarkerPath(stackId), DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write the armory static rebuild marker for stack {StackId}.", stackId);
        }
    }

    private string StylingConfigPath(string stackId) => Path.Combine(_options.StackRootPath(stackId), StylingConfigFileName);

    private string LayoutConfigPath(string stackId) => Path.Combine(_options.StackRootPath(stackId), LayoutConfigFileName);

    /// <summary>
    /// Stages uploads under the manager data volume so large trees can be copied into stack volumes
    /// daemon-side instead of streaming multi-GB tar archives through the Docker CLI.
    /// </summary>
    private string CreateUploadStagingDir(string stackId)
    {
        var dir = Path.Combine(_options.StackRootPath(stackId), ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<bool> HasArmoryDatasetOnVolumeAsync(
        ManagedStackEntity stack,
        string volumeName,
        CancellationToken cancellationToken)
    {
        foreach (var folder in DataMarkerFolders)
        {
            if (await _remoteEngine.VolumeSubdirExistsAsync(stack, volumeName, folder, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean up temp path {Path}", path);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean up temp file {Path}", path);
        }
    }
}
