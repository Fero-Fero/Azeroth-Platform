using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Manager-side service that computes the replicated stack registry and pushes it to each stack's client
/// container (see <see cref="IStackRegistryService"/>). Distribution reuses the docker-exec channel
/// (<see cref="IClientContainerService.PushPortalAsync"/>), so it works identically for local and
/// external stacks without the manager reaching any player-facing port.
/// </summary>
public sealed class StackRegistryService : IStackRegistryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AzerothCoreDbContext _dbContext;
    private readonly ILauncherPortalService _launcherPortal;
    private readonly IClientContainerService _clientContainer;
    private readonly IManifestSigningKeyProvider _signingKeys;
    private readonly ILogger<StackRegistryService> _logger;

    public StackRegistryService(
        AzerothCoreDbContext dbContext,
        ILauncherPortalService launcherPortal,
        IClientContainerService clientContainer,
        IManifestSigningKeyProvider signingKeys,
        ILogger<StackRegistryService> logger)
    {
        _dbContext = dbContext;
        _launcherPortal = launcherPortal;
        _clientContainer = clientContainer;
        _signingKeys = signingKeys;
        _logger = logger;
    }

    public async Task<StackPortalDocument> BuildDocumentAsync(CancellationToken cancellationToken = default)
    {
        // Reuse the launcher profiles aggregation (visibility, display name, realmlist host resolution,
        // template/accent) so the registry matches what the manager already advertises.
        var profiles = await _launcherPortal.GetProfilesAsync(cancellationToken);

        var stacks = await _dbContext.ManagedStacks
            .AsNoTracking()
            .Where(s => s.LauncherVisible)
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        var revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var registry = new List<StackRegistryEntry>();
        foreach (var profile in profiles.Profiles)
        {
            if (!stacks.TryGetValue(profile.StackId, out var stack))
            {
                continue;
            }

            var host = RealmlistHostResolver.NormalizeHost(profile.RealmlistHost ?? string.Empty);
            var hasHost = !string.IsNullOrWhiteSpace(host);

            var portalUrl = stack.ClientEnabled && stack.ClientPort > 0 && hasHost
                ? $"http://{host}:{stack.ClientPort}"
                : string.Empty;
            var armoryUrl = profile.ArmoryPort > 0 && hasHost
                ? $"http://{host}:{profile.ArmoryPort}"
                : string.Empty;

            // Each stack's own container serves its effective branding at /branding/*; advertise the
            // relative path only when an effective image exists (per-stack override or global default).
            var hasBackground = await _launcherPortal
                .ResolveEffectiveProfileAssetAsync(profile.StackId, LauncherAssetKind.Background, cancellationToken) is not null;
            var hasLogo = await _launcherPortal
                .ResolveEffectiveProfileAssetAsync(profile.StackId, LauncherAssetKind.Logo, cancellationToken) is not null;

            // Each stack serves its own news at /news; advertise it only when the stack has a published
            // (non-draft) article so the launcher won't fetch an empty feed.
            var hasNews = (await _launcherPortal.GetStackNewsAsync(profile.StackId, includeDrafts: false, cancellationToken)).Count > 0;

            registry.Add(new StackRegistryEntry
            {
                StackId = profile.StackId,
                DisplayName = profile.DisplayName,
                Description = profile.Description,
                PortalUrl = portalUrl,
                RealmlistHost = host,
                RealmlistPort = profile.RealmlistPort,
                ArmoryPort = profile.ArmoryPort,
                ArmoryUrl = armoryUrl,
                Template = profile.Template,
                AccentColor = profile.AccentColor,
                BackgroundUrl = hasBackground ? "/branding/background" : string.Empty,
                LogoUrl = hasLogo ? "/branding/logo" : string.Empty,
                NewsUrl = hasNews ? "/news" : string.Empty,
                ClientVersion = profile.ClientVersion,
                SortOrder = profile.SortOrder,
                Revision = revision,
            });
        }

        return new StackPortalDocument
        {
            SchemaVersion = 1,
            RegistryRevision = revision,
            GeneratedAt = DateTime.UtcNow,
            AppName = profiles.AppName,
            BrandingTitle = profiles.BrandingTitle,
            Template = profiles.Template,
            AccentColor = profiles.AccentColor,
            RequireLogin = true,
            ManifestPublicKey = _signingKeys.PublicKeySpkiBase64,
            // SelfStackId + Launcher are overlaid by each container from its own env/dist volume.
            SelfStackId = string.Empty,
            Launcher = new LauncherArtifactInfo(),
            Registry = registry,
        };
    }

    public async Task RebuildAndPushAsync(CancellationToken cancellationToken = default)
    {
        StackPortalDocument document;
        try
        {
            document = await BuildDocumentAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the stack registry snapshot; skipping push.");
            return;
        }

        var json = JsonSerializer.Serialize(document, JsonOptions);

        // Push to every visible stack that has a client container. Order is stable; failures are isolated.
        var targets = await _dbContext.ManagedStacks
            .AsNoTracking()
            .Where(s => s.LauncherVisible && s.ClientEnabled)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var pushed = 0;
        foreach (var stackId in targets)
        {
            try
            {
                if (await _clientContainer.PushPortalAsync(stackId, json, cancellationToken))
                {
                    pushed++;
                }

                // Push (or clear) the stack's effective branding images into its own container so it can
                // serve them at /branding/*. Done alongside the registry so any profile/asset/theme change
                // that rebuilds the registry also refreshes branding.
                await PushBrandingAsync(stackId, LauncherAssetKind.Background, "background", cancellationToken);
                await PushBrandingAsync(stackId, LauncherAssetKind.Logo, "logo", cancellationToken);

                // Push the stack's news feed (published articles + cover images) so its container serves it
                // at /news for the launcher. Done here so any news save/broadcast that triggers a re-push
                // also refreshes the launcher-facing feed.
                await PushNewsAsync(stackId, cancellationToken);
            }
            catch (Exception ex)
            {
                // A stopped/unreachable stack must not block the rest; it self-heals on the next push or
                // when the launcher reconciles from a healthy stack.
                _logger.LogWarning(ex, "Failed to push registry to stack {StackId}; it will self-heal later.", stackId);
            }
        }

        _logger.LogInformation(
            "Pushed stack registry (revision {Revision}, {Count} entries) to {Pushed}/{Targets} stacks.",
            document.RegistryRevision, document.Registry.Count, pushed, targets.Count);
    }

    /// <summary>
    /// Resolves a stack's effective branding image (per-stack override, else global default) and pushes
    /// its bytes into the stack's client container, or clears the file when no effective image exists.
    /// </summary>
    private async Task PushBrandingAsync(
        string stackId, LauncherAssetKind kind, string assetName, CancellationToken cancellationToken)
    {
        var resolved = await _launcherPortal.ResolveEffectiveProfileAssetAsync(stackId, kind, cancellationToken);
        byte[]? bytes = null;
        if (resolved is { } asset && File.Exists(asset.Path))
        {
            bytes = await File.ReadAllBytesAsync(asset.Path, cancellationToken);
        }

        await _clientContainer.PushBrandingAsync(stackId, assetName, bytes, cancellationToken);
    }

    private static readonly IReadOnlyDictionary<string, byte[]> EmptyImages = new Dictionary<string, byte[]>();

    // Manager-relative news-image routes (/api/launcher/news-image/ and /api/stacks/{id}/launcher/news-image/)
    // embedded in article bodies. Rewritten to the stack container's own /news-image/ so images resolve
    // against the stack (the manager is not in the player path).
    private static readonly Regex NewsImageRouteRegex = new(
        @"/api/(?:launcher|stacks/[^/""']+/launcher)/news-image/", RegexOptions.Compiled);

    private static readonly Regex InlineNewsImageIdRegex = new(
        @"/news-image/([a-zA-Z0-9_-]+)", RegexOptions.Compiled);

    /// <summary>
    /// Resolves a stack's published news feed + cover images and pushes them into its client container so it
    /// can serve them at <c>/news</c> and <c>/news-image/{id}</c>, or clears the feed when there is none.
    /// </summary>
    private async Task PushNewsAsync(string stackId, CancellationToken cancellationToken)
    {
        var items = await _launcherPortal.GetStackNewsAsync(stackId, includeDrafts: false, cancellationToken);
        if (items.Count == 0)
        {
            await _clientContainer.PushNewsAsync(stackId, null, EmptyImages, cancellationToken);
            return;
        }

        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var feed = new List<LauncherNewsItemDto>();
        foreach (var item in items)
        {
            feed.Add(new LauncherNewsItemDto
            {
                Id = item.Id,
                Title = item.Title,
                Date = item.Date,
                Html = NewsImageRouteRegex.Replace(item.Html ?? string.Empty, "/news-image/"),
                Tag = item.Tag,
                SortOrder = item.SortOrder,
                IsDraft = false,
                HasImage = item.HasImage,
                // Point the cover at the stack's own route; the launcher resolves it against the portal URL.
                ImageUrl = item.HasImage ? $"/news-image/{item.Id}" : null,
            });

            if (item.HasImage)
            {
                var resolved = await _launcherPortal.ResolveStackNewsImageAsync(stackId, item.Id, cancellationToken);
                if (resolved is { } asset && File.Exists(asset.Path))
                {
                    images[item.Id] = await File.ReadAllBytesAsync(asset.Path, cancellationToken);
                }
            }

            foreach (Match match in InlineNewsImageIdRegex.Matches(item.Html ?? string.Empty))
            {
                var imageId = match.Groups[1].Value;
                if (images.ContainsKey(imageId))
                {
                    continue;
                }

                var resolved = await _launcherPortal.ResolveStackNewsImageAsync(stackId, imageId, cancellationToken);
                if (resolved is { } asset && File.Exists(asset.Path))
                {
                    images[imageId] = await File.ReadAllBytesAsync(asset.Path, cancellationToken);
                }
            }
        }

        var json = JsonSerializer.Serialize(feed, JsonOptions);
        await _clientContainer.PushNewsAsync(stackId, json, images, cancellationToken);
    }
}
