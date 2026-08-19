using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Builds a per-stack <c>azeroth-platform-armory-{stackId}</c> image from the armory source baked into
/// the manager image, overlaying that stack's uploaded static web assets (and small server-side data).
/// The source is copied into a clean, per-stack working directory that serves as the build context
/// (streamed to the daemon by the docker client running inside this container).
/// </summary>
public sealed class ArmoryImageService : IArmoryImageService
{
    private readonly ArmoryOptions _options;
    private readonly ArmoryAssetsOptions _assetsOptions;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ArmoryImageService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private const string ArmoryContainerStaticRoot = "/usr/app/static";

    private static readonly string[] LiveLayoutRootFiles =
    [
        "data/armory-layout.json",
        "css/theme.css",
        "css/character-achievements.css",
        "css/character-progression.css",
        "css/azp-theme.css",
        "css/azp-layout.css",
        "layout.hbs",
        "index.hbs",
        "character.hbs",
        "character-talents.hbs",
        "character-skills.hbs",
        "character-achievements.hbs",
        "character-progression.hbs",
        "character-records.hbs",
        "connect.hbs",
        "news-list.hbs",
        "guild.hbs",
        "top-records.hbs",
        "map.hbs",
        "login.hbs",
        "register.hbs",
        "verify-email-pending.hbs",
        "verify-email.hbs",
        "choose-username.hbs",
        "account.hbs",
        "css/account.css",
        "css/guild.css",
        "css/emblems.css",
        "css/icons.css",
    ];

    public ArmoryImageService(
        IOptions<ArmoryOptions> options,
        IOptions<ArmoryAssetsOptions> assetsOptions,
        IRemoteEngineService remoteEngine,
        IServiceScopeFactory scopeFactory,
        ILogger<ArmoryImageService> logger)
    {
        _options = options.Value;
        _assetsOptions = assetsOptions.Value;
        _remoteEngine = remoteEngine;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public string ImageNameFor(string stackId)
    {
        // Derive a per-stack tag from the configured base image name: {name}-{stackId}[:tag].
        var baseImage = _options.ImageName;
        var colon = baseImage.LastIndexOf(':');
        var hasTag = colon > 0 && !baseImage.AsSpan(colon + 1).Contains('/');
        var name = hasTag ? baseImage[..colon] : baseImage;
        var tag = hasTag ? baseImage[(colon + 1)..] : "latest";
        return $"{name}-{stackId}:{tag}";
    }

    public async Task EnsureImageAsync(string stackId, CancellationToken cancellationToken = default)
    {
        if (await ImageExistsAsync(stackId, cancellationToken))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check now that we hold the lock; another caller may have just built it.
            if (await ImageExistsAsync(stackId, cancellationToken))
            {
                return;
            }

            await BuildImageAsync(stackId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RebuildImageAsync(string stackId, CancellationToken cancellationToken = default)
    {
        // Unconditional rebuild: unlike EnsureImageAsync we do NOT short-circuit on an existing image,
        // so edits to the armory source and this stack's uploaded static assets take effect.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await BuildImageAsync(stackId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SyncLiveLayoutAsync(string stackId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        var stack = await db.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        if (stack is null || !stack.IncludeArmory || !stack.ArmoryEnabled)
        {
            return;
        }

        var staticPath = _assetsOptions.StaticPathFor(stackId);
        if (!Directory.Exists(staticPath))
        {
            return;
        }

        EnsureLayoutTemplates(staticPath);
        EnsureLayoutShell(staticPath, stackId);

        var containerName = $"{DockerComposeOverrideGenerator.GetContainerPrefix(stackId, stack.StackName)}-armory";
        foreach (var relativePath in LiveLayoutRootFiles)
        {
            var sourcePath = Path.Combine(staticPath, relativePath);
            var destinationPath = $"{ArmoryContainerStaticRoot}/{relativePath.Replace('\\', '/')}";
            await _remoteEngine.CopyFileToContainerAsync(
                stack,
                containerName,
                sourcePath,
                destinationPath,
                cancellationToken);
        }

        var imgDir = Path.Combine(staticPath, "img");
        if (Directory.Exists(imgDir))
        {
            foreach (var wallpaperPath in Directory.EnumerateFiles(imgDir, "azp-wallpaper.*"))
            {
                var fileName = Path.GetFileName(wallpaperPath);
                var destinationPath = $"{ArmoryContainerStaticRoot}/img/{fileName}";
                await _remoteEngine.CopyFileToContainerAsync(
                    stack,
                    containerName,
                    wallpaperPath,
                    destinationPath,
                    cancellationToken);
            }

            foreach (var faviconPath in Directory.EnumerateFiles(imgDir, "azp-favicon.*"))
            {
                var fileName = Path.GetFileName(faviconPath);
                var destinationPath = $"{ArmoryContainerStaticRoot}/img/{fileName}";
                await _remoteEngine.CopyFileToContainerAsync(
                    stack,
                    containerName,
                    faviconPath,
                    destinationPath,
                    cancellationToken);
            }
        }

        var partialsDir = Path.Combine(staticPath, "partials");
        if (!Directory.Exists(partialsDir))
        {
            return;
        }

        foreach (var partialPath in Directory.EnumerateFiles(partialsDir))
        {
            var fileName = Path.GetFileName(partialPath);
            if (!fileName.StartsWith("widget-", StringComparison.Ordinal)
                && !fileName.Equals("layout-grid.hbs", StringComparison.Ordinal)
                && !fileName.Equals("armory-navbar.hbs", StringComparison.Ordinal)
                && !fileName.Equals("character-header.hbs", StringComparison.Ordinal)
                && !fileName.Equals("character-subnav.hbs", StringComparison.Ordinal)
                && !fileName.Equals("character-overview-cards.hbs", StringComparison.Ordinal)
                && !fileName.Equals("stat-panel.hbs", StringComparison.Ordinal))
            {
                continue;
            }

            var destinationPath = $"{ArmoryContainerStaticRoot}/partials/{fileName}";
            await _remoteEngine.CopyFileToContainerAsync(
                stack,
                containerName,
                partialPath,
                destinationPath,
                cancellationToken);
        }
    }

    private void EnsureLayoutShell(string staticPath, string stackId)
    {
        EnsureLayoutTemplate(staticPath);
        EnsureThemeStylesheet(staticPath, stackId);
        EnsureLayoutStylesheet(staticPath, stackId);
        EnsureResponsiveStylesheet(staticPath);
        ArmoryLayoutTheme.EnsureLayoutStylesheetLinked(staticPath);
        InjectResponsiveStylesheet(staticPath);
        InjectWallpaper(staticPath, stackId);
        InjectFavicon(staticPath);
    }

    private void EnsureLayoutTemplate(string staticPath)
    {
        var layoutPath = Path.Combine(staticPath, "layout.hbs");
        if (File.Exists(layoutPath))
        {
            return;
        }

        var sourceStatic = Path.Combine(_options.SourcePath, "static");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "layout.hbs");
    }

    private async Task<bool> ImageExistsAsync(string stackId, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunAsync("docker", $"images -q {ImageNameFor(stackId)}", cancellationToken);
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
    }

    private async Task BuildImageAsync(string stackId, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_options.SourcePath))
        {
            throw new InvalidOperationException(
                $"Armory source not found at '{_options.SourcePath}'. Ensure the Dockerfile copies frontend-armory/ into the image.");
        }

        var imageName = ImageNameFor(stackId);
        var workPath = Path.Combine(_options.WorkPath, stackId);

        // Copy the source into a clean, per-stack working directory (dropping node_modules/build/logs and
        // the large model-viewer assets) that acts as the build context.
        if (Directory.Exists(workPath))
        {
            Directory.Delete(workPath, recursive: true);
        }
        Directory.CreateDirectory(workPath);
        CopyDirectory(_options.SourcePath, workPath);

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
            var stack = await db.ManagedStacks
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);

            var staticStaging = Path.Combine(Path.GetTempPath(), "azp-armory-image-static", stackId, Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staticStaging);

                var staticVolume = DockerComposeOverrideGenerator.ArmoryStaticVolumeName(stackId);
                var staticSummary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, staticVolume, cancellationToken);
                if (stack is not null && staticSummary.VolumeExists && staticSummary.FileCount > 0)
                {
                    _logger.LogInformation(
                        "Fetching uploaded armory static bundle for stack {StackId} from volume {Volume}.",
                        stackId, staticVolume);
                    await _remoteEngine.FetchVolumeAsync(stack, staticVolume, staticStaging, cancellationToken);
                }
                else if (stack is null && staticSummary.VolumeExists && staticSummary.FileCount > 0)
                {
                    await _remoteEngine.FetchLocalVolumeAsync(staticVolume, staticStaging, cancellationToken);
                }

                var assetsVolume = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stackId);
                var assetsSummary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, assetsVolume, cancellationToken);
                if (stack is not null && assetsSummary.VolumeExists && assetsSummary.FileCount > 0)
                {
                    var dataStaging = Path.Combine(staticStaging, "data");
                    Directory.CreateDirectory(dataStaging);
                    foreach (var subdir in new[] { "dbc", "dbc_transmog", "progression" })
                    {
                        try
                        {
                            await _remoteEngine.FetchVolumeSubdirAsync(
                                stack, assetsVolume, subdir, Path.Combine(dataStaging, subdir), cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Armory image build: no {Subdir}/ folder in assets volume for stack {StackId}.", subdir, stackId);
                        }
                    }
                }

                // Generated styling/layout files still live on the manager; overlay them last.
                var managerStatic = _assetsOptions.StaticPathFor(stackId);
                if (Directory.Exists(managerStatic))
                {
                    _logger.LogInformation(
                        "Overlaying manager-side armory styling assets for stack {StackId} from {Dir}.",
                        stackId, managerStatic);
                    CopyDirectory(managerStatic, staticStaging);
                }

                if (Directory.EnumerateFileSystemEntries(staticStaging).Any())
                {
                    CopyDirectory(staticStaging, Path.Combine(workPath, "static"));
                }
            }
            finally
            {
                TryDeleteDirectory(staticStaging);
            }
        }

        PatchBundledTemplates(Path.Combine(workPath, "static"), stackId);

        // The armory's Handlebars views (error.hbs/layout.hbs/page templates) are NOT committed to the
        // into the manager image (frontend-armory/static/ must be populated from the Armory release
        // before the manager image is built) or overlaid from a per-stack armory.static.zip upload.
        // Without them the built image starts but 500s on every page ("Failed to lookup view ... in views
        // directory /usr/app/static"), so fail the build here instead of shipping a broken image.
        var templatesDir = Path.Combine(workPath, "static");
        if (!File.Exists(Path.Combine(templatesDir, "error.hbs")) || !File.Exists(Path.Combine(templatesDir, "layout.hbs")))
        {
            throw new InvalidOperationException(
                $"Armory templates are missing for stack '{stackId}' (expected static/error.hbs and " +
                "static/layout.hbs in the build context). Fix with either: (1) upload the static web bundle " +
                "(armory.static.zip, from the AzerothPlatform 'Armory' release) via Client → Armory → " +
                "Armory Assets, then rebuild the armory image; or (2) populate frontend-armory/static/ with " +
                "that bundle before building the manager image so every stack gets the templates by default.");
        }

        // IMPORTANT: `docker build <context>` streams the context from the *client's* filesystem, not
        // the daemon's. Since this process runs inside the manager container, the context must be a
        // container-visible path (workPath), NOT a host-translated path. Host translation is only for
        // bind mounts, which the daemon resolves. Passing a host path here yields "path not found".
        _logger.LogInformation("Building armory image {Image} from {Context}...", imageName, workPath);
        var (exitCode, _, stderr) = await RunAsync("docker", $"build -t {imageName} \"{workPath}\"", cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Armory image build failed (exit {exitCode}): {stderr}");
        }

        // This is the single point where uploaded static assets are actually baked into the image, so
        // clear the "static rebuild pending" marker here. That way every rebuild path (the dedicated
        // "Rebuild armory image" button, the armory "Rebuild & Restart" service action, and a DBC sync -
        // all of which rebuild the image) reconciles the prompt, not just one of them.
        ClearStaticRebuildMarker(stackId);

        _logger.LogInformation("Armory image {Image} built.", imageName);
    }

    private void PatchBundledTemplates(string staticPath, string stackId)
    {
        EnsureLayoutTemplates(staticPath);
        EnsureLayoutShell(staticPath, stackId);

        ReplaceInFile(Path.Combine(staticPath, "layout.hbs"), new Dictionary<string, string>
        {
            ["href=\"{{websiteRoot}}/top-records\">Top Records"] = "href=\"{{websiteRoot}}/top-logs\">Top Logs"
        });

        ReplaceInFile(Path.Combine(staticPath, "partials", "tracking-subnav.hbs"), new Dictionary<string, string>
        {
            ["href=\"{{websiteRoot}}/character/{{realm}}/{{name}}/records\">Records"] =
                "href=\"{{websiteRoot}}/character/{{realm}}/{{name}}/logs\">Logs"
        });

        ReplaceInFile(Path.Combine(staticPath, "partials", "character-header.hbs"), new Dictionary<string, string>
        {
            ["<div class=\"char-name title is-size-3\">{{name}}</div>"] =
                "<div class=\"char-name title is-size-3\">{{displayName}}</div>"
        });

        ReplaceInFile(Path.Combine(staticPath, "top-records.hbs"), new Dictionary<string, string>
        {
            ["<h1 class=\"title is-size-1\">Top Records</h1>"] = "<h1 class=\"title is-size-1\">Top Logs</h1>",
            ["<span class=\"tr-label\">Record:</span>"] = "<span class=\"tr-label\">Log:</span>",
            ["<option value=\"dungeon\">Dungeons</option>\n\t\t\t\t<option value=\"raid\" selected>Raids</option>"] =
                "<option value=\"dungeon\" selected>Dungeons</option>\n\t\t\t\t<option value=\"raid\">Raids</option>",
            ["{{websiteRoot}}/top-records/data"] = "{{websiteRoot}}/top-logs/data"
        });

        foreach (var file in new[] { "character-progression.hbs", "character-records.hbs" })
        {
            ReplaceInFile(Path.Combine(staticPath, file), new Dictionary<string, string>
            {
                ["data-cat=\"dungeon\" href=\"#dungeon\">Dungeon</a>\n\t<a class=\"char-subnav-tab progression-tab is-active\" data-cat=\"raid\""] =
                    "data-cat=\"dungeon\" href=\"#dungeon\">Dungeon</a>\n\t<a class=\"char-subnav-tab progression-tab\" data-cat=\"raid\"",
                ["<a class=\"char-subnav-tab progression-tab\" data-cat=\"dungeon\" href=\"#dungeon\">Dungeon</a>"] =
                    "<a class=\"char-subnav-tab progression-tab is-active\" data-cat=\"dungeon\" href=\"#dungeon\">Dungeon</a>",
                ["<div class=\"progression-cat\" data-cat=\"dungeon\"></div>\n\t<div class=\"progression-cat is-active\" data-cat=\"raid\"></div>"] =
                    "<div class=\"progression-cat is-active\" data-cat=\"dungeon\"></div>\n\t<div class=\"progression-cat\" data-cat=\"raid\"></div>",
                ["const difficultyDefaultPref = [4, 2, 7, 6, 5, 3, 1, 0];"] =
                    "const difficultyDefaultPref = [2, 6, 4, 7, 5, 3, 1, 0];"
            });
        }
    }

    private void EnsureLayoutStylesheet(string staticPath, string stackId)
    {
        var layout = ReadLayout(stackId);
        var css = ArmoryLayoutTheme.BuildCss(layout);
        var cssPath = Path.Combine(staticPath, "css", "azp-layout.css");

        if (string.IsNullOrWhiteSpace(css))
        {
            if (File.Exists(cssPath))
            {
                File.Delete(cssPath);
            }
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cssPath)!);
        File.WriteAllText(cssPath, css);

        var runtimeJson = Path.Combine(staticPath, "data", "armory-layout.json");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeJson)!);
        File.WriteAllText(runtimeJson, ArmoryLayoutSerialization.ToRuntimeJson(layout));
    }

    private static void InjectLayoutStylesheet(string staticPath)
        => ArmoryLayoutTheme.EnsureLayoutStylesheetLinked(staticPath);

    private static void EnsureResponsiveStylesheet(string staticPath)
    {
        var cssPath = Path.Combine(staticPath, "css", ArmoryResponsiveTheme.StylesheetFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(cssPath)!);
        File.WriteAllText(cssPath, ArmoryResponsiveTheme.BuildCss());
    }

    private static void InjectResponsiveStylesheet(string staticPath)
    {
        var cssPath = Path.Combine(staticPath, "css", ArmoryResponsiveTheme.StylesheetFileName);
        var layoutPath = Path.Combine(staticPath, "layout.hbs");
        if (!File.Exists(cssPath) || !File.Exists(layoutPath))
        {
            return;
        }

        var content = File.ReadAllText(layoutPath);
        if (content.Contains(ArmoryResponsiveTheme.StylesheetFileName, StringComparison.Ordinal))
        {
            return;
        }

        const string link = "    <link rel=\"stylesheet\" href=\"{{websiteRoot}}/css/azp-responsive.css\">\n";
        var updated = content.Contains("azp-layout.css", StringComparison.Ordinal)
            ? content.Replace(
                "<link rel=\"stylesheet\" href=\"{{websiteRoot}}/css/azp-layout.css\">",
                "<link rel=\"stylesheet\" href=\"{{websiteRoot}}/css/azp-layout.css\">\n" + link.TrimEnd(),
                StringComparison.Ordinal)
            : content.Contains("azp-theme.css", StringComparison.Ordinal)
                ? content.Replace(
                    "<link rel=\"stylesheet\" href=\"{{websiteRoot}}/css/azp-theme.css\">",
                    "<link rel=\"stylesheet\" href=\"{{websiteRoot}}/css/azp-theme.css\">\n" + link.TrimEnd(),
                    StringComparison.Ordinal)
                : content.Contains("</head>", StringComparison.OrdinalIgnoreCase)
                    ? content.Replace("</head>", link + "</head>", StringComparison.OrdinalIgnoreCase)
                    : link + content;

        File.WriteAllText(layoutPath, updated);
    }

    private ArmoryLayoutDto ReadLayout(string stackId)
    {
        try
        {
            var path = Path.Combine(_assetsOptions.StackRootPath(stackId), "armory-layout.json");
            if (!File.Exists(path))
            {
                return ArmoryLayoutDefaults.Default();
            }

            var layout = ArmoryLayoutSerialization.FromRuntimeJson(File.ReadAllText(path));
            return ArmoryLayoutDefaults.Normalize(layout);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read armory layout for stack {StackId}; using default.", stackId);
            return ArmoryLayoutDefaults.Default();
        }
    }

    private void EnsureThemeStylesheet(string staticPath, string stackId)
    {
        var styling = ReadStyling(stackId);
        var css = ArmoryStylingTheme.BuildCss(styling);
        var cssPath = Path.Combine(staticPath, "css", "azp-theme.css");

        if (string.IsNullOrWhiteSpace(css))
        {
            if (File.Exists(cssPath))
            {
                File.Delete(cssPath);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cssPath)!);
        File.WriteAllText(cssPath, css);
    }

    private static readonly Regex AzpFaviconLinkRegex = new(
        @"\s*<link rel=""icon""[^>]*azp-favicon[^>]*>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Injects or updates a <c>&lt;link rel="icon"&gt;</c> tag in <c>layout.hbs</c> when the operator
    /// uploaded a custom favicon at <c>static/img/azp-favicon.*</c>.
    /// </summary>
    private static void InjectFavicon(string staticPath)
    {
        var layoutPath = Path.Combine(staticPath, "layout.hbs");
        if (!File.Exists(layoutPath))
        {
            return;
        }

        var faviconRelative = FindFaviconFile(staticPath);
        var content = File.ReadAllText(layoutPath);
        var stripped = AzpFaviconLinkRegex.Replace(content, "\n");

        if (faviconRelative is null)
        {
            if (stripped != content)
            {
                File.WriteAllText(layoutPath, stripped);
            }

            return;
        }

        var link = $"    <link rel=\"icon\" href=\"{{{{websiteRoot}}}}/{faviconRelative}\">\n";
        var updated = stripped.Contains("</head>", StringComparison.OrdinalIgnoreCase)
            ? stripped.Replace("</head>", link + "</head>", StringComparison.OrdinalIgnoreCase)
            : link + stripped;

        if (updated != content)
        {
            File.WriteAllText(layoutPath, updated);
        }
    }

    private static string? FindFaviconFile(string staticPath)
    {
        var imgDir = Path.Combine(staticPath, "img");
        if (!Directory.Exists(imgDir))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(imgDir, "azp-favicon.*"))
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".ico" or ".png" or ".svg" or ".webp" or ".gif")
            {
                return "img/" + Path.GetFileName(path);
            }
        }

        return null;
    }

    private static void InjectThemeStylesheet(string staticPath)
    {
        var cssPath = Path.Combine(staticPath, "css", "azp-theme.css");
        var layoutPath = Path.Combine(staticPath, "layout.hbs");
        if (!File.Exists(cssPath) || !File.Exists(layoutPath))
        {
            return;
        }

        var content = File.ReadAllText(layoutPath);
        if (content.Contains("azp-theme.css", StringComparison.Ordinal))
        {
            return;
        }

        const string link = "    <link rel=\"stylesheet\" href=\"{{websiteRoot}}/css/azp-theme.css\">\n";
        var updated = content.Contains("</head>", StringComparison.OrdinalIgnoreCase)
            ? content.Replace("</head>", link + "</head>", StringComparison.OrdinalIgnoreCase)
            : link + content;

        File.WriteAllText(layoutPath, updated);
    }

    private static readonly string[] ImageWallpaperExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif" };

    private static readonly Regex AzpWallpaperImageUrlRegex = new(
        @"(class=""azp-wallpaper__img""[^>]*style=""[^""]*background-image:url\(')[^']*(')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Injects a full-viewport wallpaper image layer into <c>layout.hbs</c>. The paired CSS lives in the
    /// generated <c>azp-theme.css</c>. Default template wallpapers are expected under
    /// <c>static/img/bg/wallpaper_{classic|tbc|wotlk}.jpg</c>; operator-uploaded custom wallpapers live at
    /// <c>static/img/azp-wallpaper.*</c> and apply only when the Custom template is selected.
    /// </summary>
    private void InjectWallpaper(string staticPath, string stackId)
    {
        var layoutPath = Path.Combine(staticPath, "layout.hbs");
        if (!File.Exists(layoutPath))
        {
            return;
        }

        var wallpaper = ResolveWallpaper(staticPath, stackId);
        if (wallpaper is null)
        {
            return;
        }

        var relativePath = wallpaper;
        var src = "{{websiteRoot}}/" + relativePath;
        var markup =
            $"<div class=\"azp-wallpaper\" aria-hidden=\"true\" style=\"position:fixed;inset:0;z-index:0;overflow:hidden;pointer-events:none;\">"
            + $"<div class=\"azp-wallpaper__img\" style=\"background-image:url('{src}');position:absolute;inset:0;background-position:center top;background-size:cover;background-repeat:no-repeat;\"></div>"
            + "<div class=\"azp-wallpaper__overlay\" style=\"position:absolute;inset:0;\"></div></div>";

        var content = File.ReadAllText(layoutPath);
        string updated;
        if (content.Contains("azp-wallpaper", StringComparison.Ordinal))
        {
            updated = AzpWallpaperImageUrlRegex.Replace(content, $"$1{src}$2");
        }
        else
        {
            var match = Regex.Match(content, "<body[^>]*>", RegexOptions.IgnoreCase);
            updated = match.Success
                ? content.Insert(match.Index + match.Length, "\n" + markup)
                : markup + "\n" + content;
        }

        if (content != updated)
        {
            File.WriteAllText(layoutPath, updated);
            _logger.LogInformation(
                "Injected armory wallpaper for stack {StackId}: {Path}",
                stackId, relativePath);
        }
    }

    /// <summary>
    /// Overwrites layout-aware Handlebars templates from the bundled armory source so older
    /// <c>armory.static.zip</c> uploads cannot clobber the dynamic homepage / navbar / widget partials.
    /// </summary>
    private void EnsureLayoutTemplates(string staticPath)
    {
        var sourceStatic = Path.Combine(_options.SourcePath, "static");
        if (!Directory.Exists(sourceStatic))
        {
            _logger.LogWarning(
                "Armory source static directory missing at {Path}; skipping layout template sync.",
                sourceStatic);
            return;
        }

        CopyLayoutFileIfExists(sourceStatic, staticPath, "index.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "connect.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "news-list.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "top-records.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "guild.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "character.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "character-talents.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "character-skills.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "character-achievements.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "character-progression.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "character-records.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "map.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "login.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "register.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "verify-email-pending.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "verify-email.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "choose-username.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "account.hbs");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "css/account.css");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "css/guild.css");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "css/emblems.css");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "css/icons.css");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "css/theme.css");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "css/character-achievements.css");
        CopyLayoutFileIfExists(sourceStatic, staticPath, "css/character-progression.css");

        var sourcePartials = Path.Combine(sourceStatic, "partials");
        var destPartials = Path.Combine(staticPath, "partials");
        if (!Directory.Exists(sourcePartials))
        {
            return;
        }

        Directory.CreateDirectory(destPartials);
        foreach (var file in Directory.EnumerateFiles(sourcePartials))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("widget-", StringComparison.Ordinal)
                || name.Equals("layout-grid.hbs", StringComparison.Ordinal)
                || name.Equals("armory-navbar.hbs", StringComparison.Ordinal)
                || name.Equals("datatables.hbs", StringComparison.Ordinal)
                || name.Equals("emblems.hbs", StringComparison.Ordinal)
                || name.Equals("character-header.hbs", StringComparison.Ordinal)
                || name.Equals("character-subnav.hbs", StringComparison.Ordinal)
                || name.Equals("character-overview-cards.hbs", StringComparison.Ordinal)
                || name.Equals("stat-panel.hbs", StringComparison.Ordinal)
                || name.Equals("icons.hbs", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(destPartials, name), overwrite: true);
            }
        }

        EnsureLayoutThemeRules(sourceStatic, staticPath);
    }

    private static void CopyLayoutFileIfExists(string sourceStatic, string destStatic, string relativePath)
    {
        var source = Path.Combine(sourceStatic, relativePath);
        if (!File.Exists(source))
        {
            return;
        }

        var dest = Path.Combine(destStatic, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
    }

    /// <summary>
    /// Ensures <c>theme.css</c> contains homepage grid base rules when an older static bundle omitted them.
    /// Per-widget placement still comes from generated <c>azp-layout.css</c>.
    /// </summary>
    private static void EnsureLayoutThemeRules(string sourceStatic, string destStatic)
    {
        const string marker = ".armory-layout-grid";
        var destTheme = Path.Combine(destStatic, "css", "theme.css");
        if (File.Exists(destTheme) && File.ReadAllText(destTheme).Contains(marker, StringComparison.Ordinal))
        {
            return;
        }

        var sourceTheme = Path.Combine(sourceStatic, "css", "theme.css");
        if (!File.Exists(sourceTheme))
        {
            return;
        }

        var sourceContent = File.ReadAllText(sourceTheme);
        var start = sourceContent.IndexOf("/* ===== Armory layout grid", StringComparison.Ordinal);
        if (start < 0)
        {
            return;
        }

        var end = sourceContent.IndexOf("/* ===== Panels / cards =====", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return;
        }

        var block = sourceContent[start..end].TrimEnd() + Environment.NewLine + Environment.NewLine;
        Directory.CreateDirectory(Path.GetDirectoryName(destTheme)!);
        if (File.Exists(destTheme))
        {
            File.AppendAllText(destTheme, Environment.NewLine + block);
        }
        else
        {
            File.WriteAllText(destTheme, block);
        }
    }

    private string? ResolveWallpaper(string staticPath, string stackId)
    {
        var imgDir = Path.Combine(staticPath, "img");
        var styling = ReadStyling(stackId);

        string? found = null;
        if (styling.Template == ArmoryStyleTemplate.Custom)
        {
            found = FindWallpaperFile(imgDir, "azp-wallpaper");
        }

        if (found is null)
        {
            var baseName = styling.Template switch
            {
                ArmoryStyleTemplate.Classic => "wallpaper_classic",
                ArmoryStyleTemplate.Tbc => "wallpaper_tbc",
                ArmoryStyleTemplate.Wotlk => "wallpaper_wotlk",
                _ => null,
            };
            if (baseName is not null)
            {
                found = FindWallpaperFile(Path.Combine(imgDir, "bg"), baseName);
            }
        }

        if (found is null)
        {
            return null;
        }

        var absolutePath = found;
        var relative = Path.GetRelativePath(staticPath, absolutePath).Replace('\\', '/');
        return relative;
    }

    private static string? FindWallpaperFile(string dir, string baseName)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (var ext in ImageWallpaperExtensions)
        {
            var candidate = Path.Combine(dir, baseName + ext);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private ArmoryStylingDto ReadStyling(string stackId)
    {
        try
        {
            var path = Path.Combine(_assetsOptions.StackRootPath(stackId), "armory-styling.json");
            if (!File.Exists(path))
            {
                return ArmoryStylingTheme.DefaultFor(ArmoryStyleTemplate.Classic);
            }

            var styling = JsonSerializer.Deserialize<ArmoryStylingDto>(File.ReadAllText(path));
            return ArmoryStylingTheme.Normalize(styling ?? ArmoryStylingTheme.DefaultFor(ArmoryStyleTemplate.Classic));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read armory styling template for stack {StackId}; assuming Classic.", stackId);
            return ArmoryStylingTheme.DefaultFor(ArmoryStyleTemplate.Classic);
        }
    }

    private static void ReplaceInFile(string path, IReadOnlyDictionary<string, string> replacements)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var content = File.ReadAllText(path);
        var updated = content;
        foreach (var (oldValue, newValue) in replacements)
        {
            updated = updated.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        if (updated != content)
        {
            File.WriteAllText(path, updated);
        }
    }

    private void ClearStaticRebuildMarker(string stackId)
    {
        try
        {
            var marker = _assetsOptions.RebuildMarkerPath(stackId);
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: the image build already succeeded; a stale prompt is harmless.
            _logger.LogDebug(ex, "Failed to clear the armory static-rebuild marker for stack {StackId}.", stackId);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, dir);
            if (ShouldSkip(relative))
            {
                continue;
            }
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (ShouldSkip(relative))
            {
                continue;
            }
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool ShouldSkip(string relativePath)
    {
        // Skip deps/build/logs and the multi-GB 3D model-viewer assets (static/data/{mo3,meta,bone,
        // textures}); matched by folder name so it works whether copying from the baked source
        // (static/data/mo3/…) or a stack's uploaded static tree (data/mo3/…). The armory's own
        // .dockerignore drops them too, and the core armory works without them (they're sidecar-served).
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "node_modules" or "build" or ".git" or "logs"
            or "mo3" or "meta" or "bone" or "textures");
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of temp artifacts.
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
