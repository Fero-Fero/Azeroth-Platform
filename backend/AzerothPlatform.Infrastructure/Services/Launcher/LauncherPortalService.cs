using System.Text.Json;
using Ganss.Xss;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Patches;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Persists global launcher distribution config (JSON at <c>{dataRoot}/launcher/config.json</c>) and
/// per-stack profile metadata (columns on <see cref="ManagedStackEntity"/>) + branding assets on disk,
/// and aggregates them into the profiles document consumed by the launcher.
/// </summary>
public sealed class LauncherPortalService : ILauncherPortalService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly AzerothCoreDbContext _dbContext;
    private readonly DockerOptions _dockerOptions;
    private readonly MigrationOptions _migrationOptions;
    private readonly ILogger<LauncherPortalService> _logger;

    private readonly string _baseDir;
    private readonly string _globalDir;

    public LauncherPortalService(
        AzerothCoreDbContext dbContext,
        IOptions<DockerOptions> dockerOptions,
        IOptions<MigrationOptions> migrationOptions,
        ILogger<LauncherPortalService> logger)
    {
        _dbContext = dbContext;
        _dockerOptions = dockerOptions.Value;
        _migrationOptions = migrationOptions.Value;
        _logger = logger;

        _baseDir = Path.IsPathRooted(_dockerOptions.BuildsPath)
            ? _dockerOptions.BuildsPath
            : Path.GetFullPath(_dockerOptions.BuildsPath);

        var dataRoot = Path.GetDirectoryName(_baseDir.TrimEnd('/', '\\')) ?? _baseDir;
        _globalDir = Path.Combine(dataRoot, "launcher");
    }

    private string ConfigPath => Path.Combine(_globalDir, "config.json");
    private string GlobalAssetsDir => Path.Combine(_globalDir, "assets");

    // Shipped alongside the app (see AzerothPlatform.Api.csproj Content include).
    private static string TemplatesDir => Path.Combine(AppContext.BaseDirectory, "LauncherTemplates");

    /// <summary>Hard-coded style templates. Asset files are supplied under LauncherTemplates/{id}/.</summary>
    private static readonly IReadOnlyList<LauncherTemplateDto> Templates =
    [
        new() { Id = "classic", Name = "Classic (Vanilla)", AccentColor = "#C8A24B",
            Description = "Warm gold styling evoking the original World of Warcraft." },
        new() { Id = "tbc", Name = "The Burning Crusade", AccentColor = "#5F9B3A",
            Description = "Fel-green styling for The Burning Crusade." },
        new() { Id = "wotlk", Name = "Wrath of the Lich King", AccentColor = "#4FA8D8",
            Description = "Icy blue styling for Wrath of the Lich King." }
    ];

    /// <summary>Returns the matching template, or null when the id is empty/"none" (use uploaded branding).</summary>
    private static LauncherTemplateDto? FindTemplate(string? id) =>
        Templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Normalizes a stored template id to a known id, or empty string for "None".</summary>
    private static string NormalizeTemplateId(string? id) => FindTemplate(id)?.Id ?? string.Empty;

    // ===== Global config =====

    public async Task<LauncherDistributionConfigDto> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var config = new LauncherDistributionConfigDto();
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(ConfigPath, cancellationToken);
                config = JsonSerializer.Deserialize<LauncherDistributionConfigDto>(json, JsonOptions) ?? config;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read launcher config; returning defaults");
            }
        }

        config.HasBackground = ResolveAsset(GlobalAssetsDir, "background") is not null;
        config.HasLogo = ResolveAsset(GlobalAssetsDir, "logo") is not null;
        config.HasIcon = ResolveAsset(GlobalAssetsDir, "icon") is not null;
        config.Template = NormalizeTemplateId(config.Template);

        // When the operator hasn't pinned a public URL, default the launcher's baked server address to this
        // host's LAN IP on the manager port so a "local setup" launcher connects on the LAN out of the box
        // (rather than the unreachable localhost fallback). Only when a real LAN/public host is configured
        // (HOST_LAN_IP / Migrations:RealmlistHost); loopback is left blank so the launcher keeps its
        // localhost default for same-machine dev.
        if (string.IsNullOrWhiteSpace(config.PublicBaseUrl))
        {
            var host = _migrationOptions.RealmlistHost?.Trim();
            if (!string.IsNullOrWhiteSpace(host)
                && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !host.StartsWith("127.", StringComparison.Ordinal))
            {
                config.PublicBaseUrl = $"http://{host}:8080";
            }
        }

        return config;
    }

    public async Task<LauncherDistributionConfigDto> SaveConfigAsync(LauncherDistributionConfigDto config, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_globalDir);
        // Persist only the editable fields; asset flags are derived on read.
        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(ConfigPath, json, cancellationToken);
        return await GetConfigAsync(cancellationToken);
    }

    public async Task<LauncherDistributionConfigDto> SaveGlobalAssetAsync(
        LauncherAssetKind kind, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(GlobalAssetsDir);
        await StoreBrandingAssetAsync(GlobalAssetsDir, kind, fileName, content, cancellationToken);
        return await GetConfigAsync(cancellationToken);
    }

    public (string Path, string ContentType)? ResolveGlobalAsset(LauncherAssetKind kind)
    {
        var path = ResolveAsset(GlobalAssetsDir, AssetBaseName(kind));
        return path is null ? null : (path, GuessContentType(path));
    }

    // ===== Hard-coded style templates =====

    public IReadOnlyList<LauncherTemplateDto> GetTemplates() =>
        Templates.Select(t => new LauncherTemplateDto
        {
            Id = t.Id,
            Name = t.Name,
            Description = t.Description,
            AccentColor = t.AccentColor,
            BackgroundUrl = TemplateAssetPath(t.Id, "background") is not null ? $"/api/launcher/templates/{t.Id}/background" : null,
            LogoUrl = TemplateAssetPath(t.Id, "logo") is not null ? $"/api/launcher/templates/{t.Id}/logo" : null,
            IconUrl = TemplateAssetPath(t.Id, "icon") is not null ? $"/api/launcher/templates/{t.Id}/icon" : null
        }).ToList();

    public (string Path, string ContentType)? ResolveTemplateAsset(string templateId, string asset)
    {
        var baseName = string.Equals(asset, "background", StringComparison.OrdinalIgnoreCase) ? "background"
            : string.Equals(asset, "logo", StringComparison.OrdinalIgnoreCase) ? "logo"
            : string.Equals(asset, "icon", StringComparison.OrdinalIgnoreCase) ? "icon"
            : null;
        if (baseName is null) { return null; }

        var path = TemplateAssetPath(templateId, baseName);
        return path is null ? null : (path, GuessContentType(path));
    }

    /// <summary>Absolute path to a template asset when the template id is known and the file exists.</summary>
    private static string? TemplateAssetPath(string templateId, string baseName)
    {
        if (!Templates.Any(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return ResolveAsset(Path.Combine(TemplatesDir, templateId.ToLowerInvariant()), baseName);
    }

    // ===== Profiles document =====

    public async Task<LauncherProfilesDto> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var config = await GetConfigAsync(cancellationToken);
        var template = FindTemplate(config.Template);

        // Default branding precedence: an uploaded global asset wins, otherwise fall back to the
        // selected style template's shipped asset (when a template is selected at all).
        var templateBackground = template is not null && TemplateAssetPath(template.Id, "background") is not null
            ? $"/api/launcher/templates/{template.Id}/background" : null;
        var templateLogo = template is not null && TemplateAssetPath(template.Id, "logo") is not null
            ? $"/api/launcher/templates/{template.Id}/logo" : null;

        var dto = new LauncherProfilesDto
        {
            AppName = config.AppName,
            BrandingTitle = config.BrandingTitle,
            GameExecutable = config.GameExecutable,
            LaunchArguments = config.LaunchArguments,
            ClientVersion = config.ClientVersion,
            Template = template?.Id ?? string.Empty,
            AccentColor = template?.AccentColor ?? string.Empty,
            DefaultBackgroundUrl = config.HasBackground ? "/api/launcher/assets/background" : templateBackground,
            DefaultLogoUrl = config.HasLogo ? "/api/launcher/assets/logo" : templateLogo,
            GlobalNewsUrl = NewsExists(GlobalNewsDir) ? "/api/launcher/news" : null
        };

        var stacks = await _dbContext.ManagedStacks
            .Where(s => s.LauncherVisible)
            .OrderBy(s => s.LauncherSortOrder)
            .ThenBy(s => s.StackName)
            .ToListAsync(cancellationToken);

        foreach (var stack in stacks)
        {
            var stackRoot = Path.Combine(_baseDir, stack.Id);
            var assetsDir = MigrationLayout.LauncherProfileDir(stackRoot);
            var profileBase = $"/api/stacks/{stack.Id}/launcher/profile-asset";
            var stackTemplate = ResolveEffectiveStackTemplate(stack, template);

            // Per-stack theme comes from patch launcher.json overrides when set; otherwise the global theme.
            // Wallpaper and logo can still be overridden per stack.
            dto.Profiles.Add(new LauncherProfileDto
            {
                StackId = stack.Id,
                DisplayName = ResolveDisplayName(stack),
                Description = stack.LauncherDescription,
                SortOrder = stack.LauncherSortOrder,
                RealmlistHost = ResolveRealmlistHost(stack),
                RealmlistPort = stack.AuthServerPort,
                // Advertise the armory port only when the armory is enabled so the launcher's
                // "View all news" shortcut targets a stack that actually has an armory to open.
                ArmoryPort = stack.IncludeArmory && stack.ArmoryEnabled ? stack.ArmoryPort : 0,
                BackgroundUrl = ResolveAsset(assetsDir, "background") is not null ? $"{profileBase}/background"
                    : ResolveStackDefaultBackgroundUrl(stackTemplate, config, dto.DefaultBackgroundUrl),
                LogoUrl = ResolveAsset(assetsDir, "logo") is not null ? $"{profileBase}/logo"
                    : ResolveStackDefaultLogoUrl(stackTemplate, config, dto.DefaultLogoUrl),
                NewsUrl = NewsExists(StackNewsDir(stack.Id)) ? $"/api/stacks/{stack.Id}/launcher/news" : null,
                Template = stackTemplate?.Id ?? string.Empty,
                AccentColor = stackTemplate?.AccentColor ?? string.Empty,
                // Effective client-version label: the per-stack value if set, else the global default.
                ClientVersion = string.IsNullOrWhiteSpace(stack.LauncherClientVersion)
                    ? config.ClientVersion
                    : stack.LauncherClientVersion
            });
        }

        return dto;
    }

    // ===== Per-stack profile config =====

    public async Task<LauncherProfileConfigDto> GetProfileAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        return await ToProfileConfigAsync(stack);
    }

    public async Task<LauncherProfileConfigDto> SaveProfileAsync(LauncherProfileConfigDto profile, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(profile.StackId, cancellationToken);

        stack.LauncherVisible = profile.Visible;
        stack.LauncherDisplayName = (profile.DisplayName ?? string.Empty).Trim();
        stack.LauncherDescription = (profile.Description ?? string.Empty).Trim();
        stack.LauncherSortOrder = profile.SortOrder;
        stack.RealmlistHostOverride = (profile.RealmlistHostOverride ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(stack.RealmlistHostOverride))
        {
            stack.RealmlistHostOverride = RealmlistHostResolver.NormalizeHost(stack.RealmlistHostOverride);
        }
        stack.LauncherClientVersion = (profile.ClientVersion ?? string.Empty).Trim();
        stack.LauncherTemplate = NormalizeTemplateId(profile.Template);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await ToProfileConfigAsync(stack);
    }

    public async Task<LauncherProfileConfigDto> SaveProfileAssetAsync(
        string stackId, LauncherAssetKind kind, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var assetsDir = MigrationLayout.LauncherProfileDir(Path.Combine(_baseDir, stack.Id));
        Directory.CreateDirectory(assetsDir);
        await StoreBrandingAssetAsync(assetsDir, kind, fileName, content, cancellationToken);
        return await ToProfileConfigAsync(stack);
    }

    public async Task<LauncherProfileConfigDto> DeleteProfileAssetAsync(
        string stackId, LauncherAssetKind kind, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var assetsDir = MigrationLayout.LauncherProfileDir(Path.Combine(_baseDir, stack.Id));
        ClearAssets(assetsDir, AssetBaseName(kind));
        return await ToProfileConfigAsync(stack);
    }

    public async Task<(string Path, string ContentType)?> ResolveProfileAssetAsync(
        string stackId, string asset, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var assetsDir = MigrationLayout.LauncherProfileDir(Path.Combine(_baseDir, stack.Id));

        var baseName = string.Equals(asset, "background", StringComparison.OrdinalIgnoreCase) ? "background"
            : string.Equals(asset, "logo", StringComparison.OrdinalIgnoreCase) ? "logo"
            : null;
        if (baseName is null) { return null; }

        var path = ResolveAsset(assetsDir, baseName);
        return path is null ? null : (path, GuessContentType(path));
    }

    public async Task<(string Path, string ContentType)?> ResolveEffectiveProfileAssetAsync(
        string stackId, LauncherAssetKind kind, CancellationToken cancellationToken = default)
    {
        if (kind is not (LauncherAssetKind.Background or LauncherAssetKind.Logo))
        {
            return null;
        }

        var baseName = AssetBaseName(kind);

        // 1. Per-stack override.
        var stackDir = MigrationLayout.LauncherProfileDir(Path.Combine(_baseDir, stackId));
        var perStack = ResolveAsset(stackDir, baseName);
        if (perStack is not null) { return (perStack, GuessContentType(perStack)); }

        // 2. Global uploaded default.
        var global = ResolveAsset(GlobalAssetsDir, baseName);
        if (global is not null) { return (global, GuessContentType(global)); }

        // 3. Per-stack or global theme's shipped asset.
        var stack = await GetStackAsync(stackId, cancellationToken);
        var config = await GetConfigAsync(cancellationToken);
        var globalTemplate = FindTemplate(config.Template);
        var stackTemplate = ResolveEffectiveStackTemplate(stack, globalTemplate);
        if (stackTemplate is not null)
        {
            var templatePath = TemplateAssetPath(stackTemplate.Id, baseName);
            if (templatePath is not null) { return (templatePath, GuessContentType(templatePath)); }
        }

        return null;
    }

    private static LauncherTemplateDto? ResolveEffectiveStackTemplate(
        ManagedStackEntity stack,
        LauncherTemplateDto? globalTemplate)
    {
        var perStack = FindTemplate(stack.LauncherTemplate);
        return perStack ?? globalTemplate;
    }

    private static string? ResolveStackDefaultBackgroundUrl(
        LauncherTemplateDto? stackTemplate,
        LauncherDistributionConfigDto config,
        string? globalDefaultBackgroundUrl)
    {
        if (config.HasBackground)
        {
            return "/api/launcher/assets/background";
        }

        if (stackTemplate is not null && TemplateAssetPath(stackTemplate.Id, "background") is not null)
        {
            return $"/api/launcher/templates/{stackTemplate.Id}/background";
        }

        return globalDefaultBackgroundUrl;
    }

    private static string? ResolveStackDefaultLogoUrl(
        LauncherTemplateDto? stackTemplate,
        LauncherDistributionConfigDto config,
        string? globalDefaultLogoUrl)
    {
        if (config.HasLogo)
        {
            return "/api/launcher/assets/logo";
        }

        if (stackTemplate is not null && TemplateAssetPath(stackTemplate.Id, "logo") is not null)
        {
            return $"/api/launcher/templates/{stackTemplate.Id}/logo";
        }

        return globalDefaultLogoUrl;
    }

    // ===== News (global + per-stack) =====

    private const string NewsFileName = "news.json";
    private string GlobalNewsDir => Path.Combine(_globalDir, "news");
    private string StackNewsDir(string stackId) =>
        Path.Combine(MigrationLayout.LauncherProfileDir(Path.Combine(_baseDir, stackId)), "news");

    private static readonly HtmlSanitizer NewsSanitizer = CreateNewsSanitizer();

    private static HtmlSanitizer CreateNewsSanitizer()
    {
        // Ganss.Xss ships a safe default allow-list (headings, paragraphs, lists, links, images,
        // blockquote, tables, inline styles with a CSS property allow-list). Also permit class so the
        // shared .news-content styling hooks survive.
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedAttributes.Add("class");
        return sanitizer;
    }

    private static bool NewsExists(string dir) => File.Exists(Path.Combine(dir, NewsFileName));

    /// <summary>Persisted news shape; cover-image flags/urls are derived from disk at read time.</summary>
    private sealed class StoredNewsItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Html { get; set; } = string.Empty;
        public int SortOrder { get; set; }

        /// <summary>Draft articles are persisted but hidden from the launcher-facing feed.</summary>
        public bool IsDraft { get; set; }

        /// <summary>Content category shown as a colored ribbon on the cards (normalized token).</summary>
        public string Tag { get; set; } = string.Empty;
    }

    /// <summary>
    /// Allowed news content tags. Kept as a small allowlist so the UI colors stay consistent; any
    /// value outside this set is dropped to no-tag on save.
    /// </summary>
    private static readonly HashSet<string> AllowedNewsTags =
        new(StringComparer.OrdinalIgnoreCase) { "patch", "announcement", "expansion", "event", "update", "hotfix" };

    private static LauncherNewsItemDto NormalizeIncomingNewsItem(LauncherNewsItemDto item)
    {
        var id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id.Trim();
        return new LauncherNewsItemDto
        {
            Id = id,
            Title = (item.Title ?? string.Empty).Trim(),
            Date = (item.Date ?? string.Empty).Trim(),
            Html = NewsSanitizer.Sanitize(item.Html ?? string.Empty),
            SortOrder = item.SortOrder,
            IsDraft = item.IsDraft,
            Tag = NormalizeNewsTag(item.Tag),
        };
    }

    private static string NormalizeNewsTag(string? tag)
    {
        var normalized = (tag ?? string.Empty).Trim().ToLowerInvariant();
        return AllowedNewsTags.Contains(normalized) ? normalized : string.Empty;
    }

    public Task<IReadOnlyList<LauncherNewsItemDto>> GetGlobalNewsAsync(bool includeDrafts = false, CancellationToken cancellationToken = default) =>
        ReadNewsAsync(GlobalNewsDir, "/api/launcher/news-image", includeDrafts, cancellationToken);

    public Task<IReadOnlyList<LauncherNewsItemDto>> SaveGlobalNewsAsync(
        IReadOnlyList<LauncherNewsItemDto> items, CancellationToken cancellationToken = default) =>
        WriteNewsAsync(GlobalNewsDir, items, "/api/launcher/news-image", cancellationToken);

    public async Task<IReadOnlyList<LauncherNewsItemDto>> SaveGlobalNewsImageAsync(
        string itemId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await StoreNewsImageAsync(GlobalNewsDir, itemId, fileName, content, cancellationToken);
        // Editor-side call: keep drafts so the editor's list stays intact after an image upload.
        return await GetGlobalNewsAsync(includeDrafts: true, cancellationToken);
    }

    public (string Path, string ContentType)? ResolveGlobalNewsImage(string itemId)
    {
        var path = ResolveAsset(GlobalNewsDir, itemId);
        return path is null ? null : (path, GuessContentType(path));
    }

    /// <summary>Reserved id prefix for stack news articles that originate from a global broadcast.</summary>
    private const string GlobalBroadcastPrefix = "global-";

    public async Task<GlobalNewsBroadcastResult> BroadcastGlobalNewsAsync(CancellationToken cancellationToken = default)
    {
        // Only published global articles are broadcast; drafts stay on the manager until published.
        var globalDir = GlobalNewsDir;
        var globalItems = await ReadNewsAsync(globalDir, "/api/launcher/news-image", includeDrafts: false, cancellationToken);

        var stackIds = await _dbContext.ManagedStacks
            .Where(s => s.LauncherVisible)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var result = new GlobalNewsBroadcastResult
        {
            ArticleCount = globalItems.Count,
            TotalStacks = stackIds.Count
        };

        foreach (var stackId in stackIds)
        {
            try
            {
                var stackDir = StackNewsDir(stackId);
                var imageRoute = $"/api/stacks/{stackId}/launcher/news-image";

                // Keep the stack's own articles; replace any prior broadcast copies so edits/removals
                // propagate instead of piling up.
                var merged = (await ReadNewsAsync(stackDir, imageRoute, includeDrafts: true, cancellationToken))
                    .Where(i => !i.Id.StartsWith(GlobalBroadcastPrefix, StringComparison.Ordinal))
                    .ToList();

                // The stack places broadcast articles automatically: give them the highest sort orders so
                // they land as this stack's latest news with no manual reordering.
                var baseSort = merged.Count == 0 ? 0 : merged.Max(i => i.SortOrder);
                var offset = 1;
                foreach (var g in globalItems.OrderBy(i => i.SortOrder))
                {
                    merged.Add(new LauncherNewsItemDto
                    {
                        Id = GlobalBroadcastPrefix + g.Id,
                        Title = g.Title,
                        Date = g.Date,
                        Html = g.Html,
                        Tag = g.Tag,
                        IsDraft = false,
                        SortOrder = baseSort + offset++
                    });
                }

                await WriteNewsAsync(stackDir, merged, imageRoute, cancellationToken);

                // Carry each broadcast article's cover image across so it keeps its artwork.
                foreach (var g in globalItems)
                {
                    var src = ResolveAsset(globalDir, g.Id);
                    if (src is null) { continue; }

                    var destId = GlobalBroadcastPrefix + g.Id;
                    ClearAssets(stackDir, destId);
                    File.Copy(src, Path.Combine(stackDir, destId + Path.GetExtension(src)), overwrite: true);
                }

                result.Updated++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast global news to stack {StackId}.", stackId);
                result.Failures.Add($"{stackId}: {ex.Message}");
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<LauncherNewsItemDto>> GetStackNewsAsync(string stackId, bool includeDrafts = false, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        return await ReadNewsAsync(StackNewsDir(stackId), $"/api/stacks/{stackId}/launcher/news-image", includeDrafts, cancellationToken);
    }

    public async Task<IReadOnlyList<LauncherNewsItemDto>> SaveStackNewsAsync(
        string stackId, IReadOnlyList<LauncherNewsItemDto> items, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        return await WriteNewsAsync(StackNewsDir(stackId), items, $"/api/stacks/{stackId}/launcher/news-image", cancellationToken);
    }

    public async Task<IReadOnlyList<LauncherNewsItemDto>> SaveStackNewsImageAsync(
        string stackId, string itemId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        await StoreNewsImageAsync(StackNewsDir(stackId), itemId, fileName, content, cancellationToken);
        return await GetStackNewsAsync(stackId, includeDrafts: true, cancellationToken);
    }

    public async Task<IReadOnlyList<LauncherNewsItemDto>> MergeStackNewsArticleAsync(
        string stackId,
        LauncherNewsItemDto article,
        CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var dir = StackNewsDir(stackId);
        var imageRoute = $"/api/stacks/{stackId}/launcher/news-image";
        var existing = await ReadNewsAsync(dir, imageRoute, includeDrafts: true, cancellationToken);
        var merged = existing.ToList();
        var normalized = NormalizeIncomingNewsItem(article);

        var existingIndex = merged.FindIndex(item =>
            string.Equals(item.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            normalized.SortOrder = merged[existingIndex].SortOrder;
            merged[existingIndex] = normalized;
        }
        else
        {
            normalized.SortOrder = merged.Count == 0 ? 0 : merged.Max(item => item.SortOrder) + 1;
            merged.Add(normalized);
        }

        return await WriteNewsAsync(dir, merged, imageRoute, cancellationToken);
    }

    public async Task MergeStackNewsCoverFromFileAsync(
        string stackId,
        string itemId,
        string sourceImagePath,
        CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        await using var stream = File.OpenRead(sourceImagePath);
        await StoreNewsImageAsync(
            StackNewsDir(stackId),
            itemId,
            Path.GetFileName(sourceImagePath),
            stream,
            cancellationToken);
    }

    public async Task<(string Path, string ContentType)?> ResolveStackNewsImageAsync(
        string stackId, string itemId, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var path = ResolveAsset(StackNewsDir(stackId), itemId);
        return path is null ? null : (path, GuessContentType(path));
    }

    private async Task<IReadOnlyList<LauncherNewsItemDto>> ReadNewsAsync(string dir, string imageRoute, bool includeDrafts, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dir, NewsFileName);
        if (!File.Exists(path)) { return Array.Empty<LauncherNewsItemDto>(); }

        List<StoredNewsItem> stored;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            stored = JsonSerializer.Deserialize<List<StoredNewsItem>>(json, JsonOptions) ?? new List<StoredNewsItem>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read launcher news at {Path}", path);
            return Array.Empty<LauncherNewsItemDto>();
        }

        return stored
            // Drafts are withheld from the launcher-facing feed; the editor passes includeDrafts=true.
            .Where(i => includeDrafts || !i.IsDraft)
            // Highest sortOrder first so the latest article is shown first everywhere (editor,
            // launcher feed, preview). sortOrder is a stable per-item value, not an array index.
            .OrderByDescending(i => i.SortOrder)
            .Select(i =>
            {
                var hasImage = ResolveAsset(dir, i.Id) is not null;
                return new LauncherNewsItemDto
                {
                    Id = i.Id,
                    Title = i.Title,
                    Date = i.Date,
                    Html = i.Html,
                    SortOrder = i.SortOrder,
                    IsDraft = i.IsDraft,
                    Tag = i.Tag,
                    HasImage = hasImage,
                    ImageUrl = hasImage ? $"{imageRoute}/{i.Id}" : null
                };
            })
            .ToList();
    }

    private async Task<IReadOnlyList<LauncherNewsItemDto>> WriteNewsAsync(
        string dir, IReadOnlyList<LauncherNewsItemDto> items, string imageRoute, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dir);

        // Normalize: ensure ids and sanitize HTML. sortOrder is a stable, client-assigned value
        // (new articles get max+1; reordering swaps two values), so we preserve it rather than
        // reindexing by array position.
        var normalized = new List<StoredNewsItem>();
        foreach (var item in items ?? Array.Empty<LauncherNewsItemDto>())
        {
            var dto = NormalizeIncomingNewsItem(item);
            normalized.Add(new StoredNewsItem
            {
                Id = dto.Id,
                Title = dto.Title,
                Date = dto.Date,
                Html = dto.Html,
                SortOrder = dto.SortOrder,
                IsDraft = dto.IsDraft,
                Tag = dto.Tag,
            });
        }

        // Prune orphaned cover images (any {id}.* whose id is no longer present).
        var keepIds = normalized.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, NewsFileName, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (!keepIds.Contains(Path.GetFileNameWithoutExtension(name))) { TryDelete(file); }
        }

        if (normalized.Count == 0)
        {
            // Remove the list file so NewsExists() reports none (falls back to global / no news).
            TryDelete(Path.Combine(dir, NewsFileName));
        }
        else
        {
            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(dir, NewsFileName), json, cancellationToken);
        }

        // Save is an editor operation, so return the full list including drafts.
        return await ReadNewsAsync(dir, imageRoute, includeDrafts: true, cancellationToken);
    }

    /// <summary>Stores a news cover as <c>{itemId}{ext}</c>, downscaling large images (max 1280px).</summary>
    private async Task StoreNewsImageAsync(string dir, string itemId, string fileName, Stream content, CancellationToken cancellationToken)
    {
        if (!IsSafeAssetId(itemId))
        {
            throw new ArgumentException("Invalid news item id.", nameof(itemId));
        }

        Directory.CreateDirectory(dir);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext)) { ext = ".png"; }

        ClearAssets(dir, itemId);
        var target = Path.Combine(dir, itemId + ext);

        try
        {
            using var image = await Image.LoadAsync(buffer, cancellationToken);
            if (image.Width > 1280)
            {
                image.Mutate(x => x.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(1280, 1280) }));
            }
            await image.SaveAsync(target, cancellationToken);
        }
        catch
        {
            // Not a decodable/encodable image for this extension - store the bytes verbatim.
            buffer.Position = 0;
            await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await buffer.CopyToAsync(fs, cancellationToken);
        }
    }

    // ===== Helpers =====

    private async Task<ManagedStackEntity> GetStackAsync(string stackId, CancellationToken cancellationToken) =>
        await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

    private Task<LauncherProfileConfigDto> ToProfileConfigAsync(ManagedStackEntity stack)
    {
        var assetsDir = MigrationLayout.LauncherProfileDir(Path.Combine(_baseDir, stack.Id));

        return Task.FromResult(new LauncherProfileConfigDto
        {
            StackId = stack.Id,
            Visible = stack.LauncherVisible,
            DisplayName = string.IsNullOrWhiteSpace(stack.LauncherDisplayName) ? ResolveDisplayName(stack) : stack.LauncherDisplayName,
            Description = stack.LauncherDescription,
            SortOrder = stack.LauncherSortOrder,
            RealmlistHostOverride = stack.RealmlistHostOverride,
            EffectiveRealmlistHost = ResolveRealmlistHost(stack),
            RealmlistPort = stack.AuthServerPort,
            ClientVersion = stack.LauncherClientVersion,
            HasBackground = ResolveAsset(assetsDir, "background") is not null,
            HasLogo = ResolveAsset(assetsDir, "logo") is not null,
            Template = stack.LauncherTemplate ?? string.Empty
        });
    }

    private string ResolveDisplayName(ManagedStackEntity stack) =>
        !string.IsNullOrWhiteSpace(stack.LauncherDisplayName) ? stack.LauncherDisplayName
        : !string.IsNullOrWhiteSpace(stack.RealmName) ? stack.RealmName
        : stack.StackName;

    private string ResolveRealmlistHost(ManagedStackEntity stack) =>
        RealmlistHostResolver.NormalizeHost(string.IsNullOrWhiteSpace(stack.RealmlistHostOverride)
            ? _migrationOptions.RealmlistHost
            : stack.RealmlistHostOverride);

    private static string AssetBaseName(LauncherAssetKind kind) => kind switch
    {
        LauncherAssetKind.Background => "background",
        LauncherAssetKind.Logo => "logo",
        LauncherAssetKind.Icon => "icon",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Finds an asset stored as <c>{baseName}.{ext}</c> in a directory, or null.</summary>
    private static string? ResolveAsset(string dir, string baseName)
    {
        if (!Directory.Exists(dir) || !IsSafeAssetId(baseName)) { return null; }
        return Directory.EnumerateFiles(dir, baseName + ".*").FirstOrDefault();
    }

    /// <summary>
    /// News/asset ids come from clients and are used to build file paths, so restrict them to a safe
    /// token set (no separators, no <c>..</c>) to prevent path traversal.
    /// </summary>
    private static bool IsSafeAssetId(string? id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Routes a branding asset to the right storage path (icon conversion or plain store).</summary>
    private async Task StoreBrandingAssetAsync(
        string dir, LauncherAssetKind kind, string fileName, Stream content, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case LauncherAssetKind.Icon:
                await StoreIconAsync(dir, fileName, content, cancellationToken);
                break;
            case LauncherAssetKind.Background:
                // Backgrounds are static images only (the launcher doesn't render animated wallpapers),
                // so store the upload as-is like any other image asset.
                await StoreAssetAsync(dir, "background", fileName, content, cancellationToken);
                break;
            default:
                await StoreAssetAsync(dir, AssetBaseName(kind), fileName, content, cancellationToken);
                break;
        }
    }

    private static void ClearAssets(string dir, string baseName)
    {
        foreach (var existing in Directory.EnumerateFiles(dir, baseName + ".*"))
        {
            try { File.Delete(existing); } catch { /* best effort */ }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* best effort */ }
    }

    /// <summary>
    /// Stores the global app icon as <c>icon.ico</c>. An uploaded .ico is stored verbatim; any other
    /// raster image (PNG/JPG/WebP/GIF/BMP) is decoded and re-encoded to .ico (downscaled to the 256px
    /// ICO maximum) so the website can accept ordinary images.
    /// </summary>
    private async Task StoreIconAsync(string dir, string fileName, Stream content, CancellationToken cancellationToken)
    {
        foreach (var existing in Directory.EnumerateFiles(dir, "icon.*"))
        {
            try { File.Delete(existing); } catch { /* best effort */ }
        }

        var target = Path.Combine(dir, "icon.ico");

        if (fileName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            await using var raw = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await content.CopyToAsync(raw, cancellationToken);
            return;
        }

        // Buffer the upload so ImageSharp can seek while probing the format.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        try
        {
            using var image = await Image.LoadAsync(buffer, cancellationToken);
            // ICO entries max out at 256x256; scale down while preserving aspect ratio.
            if (image.Width > 256 || image.Height > 256)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(256, 256)
                }));
            }

            // Encode as PNG, then wrap it in a single-entry ICO (PNG-in-ICO, supported on Windows Vista+).
            using var png = new MemoryStream();
            await image.SaveAsync(png, new PngEncoder(), cancellationToken);
            var ico = WrapPngInIco(png.ToArray(), image.Width, image.Height);
            await File.WriteAllBytesAsync(target, ico, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not read the uploaded image. Upload a PNG, JPG, WebP, GIF, BMP or .ico file.", ex);
        }
    }

    /// <summary>
    /// Wraps PNG bytes in a minimal single-image ICO container. ICO stores 256 as a 0 in the
    /// width/height byte fields.
    /// </summary>
    private static byte[] WrapPngInIco(byte[] png, int width, int height)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // ICONDIR
        writer.Write((ushort)0);          // reserved
        writer.Write((ushort)1);          // type: 1 = icon
        writer.Write((ushort)1);          // image count

        // ICONDIRENTRY
        writer.Write((byte)(width >= 256 ? 0 : width));
        writer.Write((byte)(height >= 256 ? 0 : height));
        writer.Write((byte)0);            // color palette count
        writer.Write((byte)0);            // reserved
        writer.Write((ushort)1);          // color planes
        writer.Write((ushort)32);         // bits per pixel
        writer.Write((uint)png.Length);   // size of image data
        writer.Write((uint)22);           // offset of image data (6 + 16)

        writer.Write(png);
        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>Stores a stream as <c>{baseName}{ext}</c>, replacing any existing asset of that base.</summary>
    private static async Task StoreAssetAsync(string dir, string baseName, string fileName, Stream content, CancellationToken cancellationToken)
    {
        foreach (var existing in Directory.EnumerateFiles(dir, baseName + ".*"))
        {
            try { File.Delete(existing); } catch { /* best effort */ }
        }

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) { ext = ".bin"; }
        var target = Path.Combine(dir, baseName + ext.ToLowerInvariant());

        await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(fs, cancellationToken);
    }

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".xml" => "application/xml",
        _ => "application/octet-stream"
    };
}
