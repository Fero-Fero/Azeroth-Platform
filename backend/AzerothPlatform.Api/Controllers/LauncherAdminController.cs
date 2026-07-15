using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Website-facing administration of the launcher distribution: global branding/identity config,
/// asset uploads, and per-stack profile settings. Consumed by the manager UI (not the launcher).
/// </summary>
[Authorize]
[ApiController]
[Route("api/launcher-admin")]
public class LauncherAdminController : ControllerBase
{
    private readonly ILauncherPortalService _portal;
    private readonly IStackRegistryService _registry;
    private readonly ILogger<LauncherAdminController> _logger;

    public LauncherAdminController(
        ILauncherPortalService portal,
        IStackRegistryService registry,
        ILogger<LauncherAdminController> logger)
    {
        _portal = portal;
        _registry = registry;
        _logger = logger;
    }

    [HttpGet("config")]
    public async Task<ActionResult<LauncherDistributionConfigDto>> GetConfig(CancellationToken cancellationToken) =>
        Ok(await _portal.GetConfigAsync(cancellationToken));

    /// <summary>The hard-coded style templates (classic/tbc/wotlk) with accent colors and asset URLs.</summary>
    [HttpGet("templates")]
    public ActionResult<IReadOnlyList<LauncherTemplateDto>> GetTemplates() =>
        Ok(_portal.GetTemplates());

    [HttpPut("config")]
    public async Task<ActionResult<LauncherDistributionConfigDto>> SaveConfig(
        [FromBody] LauncherDistributionConfigDto config, CancellationToken cancellationToken)
    {
        var saved = await _portal.SaveConfigAsync(config, cancellationToken);
        // The global theme/default branding drives every stack's effective wallpaper/logo, so re-push.
        await RepushBrandingAsync(cancellationToken);
        return Ok(saved);
    }

    [HttpPost("assets/{kind}")]
    public async Task<ActionResult<LauncherDistributionConfigDto>> UploadGlobalAsset(
        string kind, IFormFile file, CancellationToken cancellationToken)
    {
        if (!TryParseKind(kind, out var assetKind))
        {
            return BadRequest(new { error = $"Unknown asset kind '{kind}'." });
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded." });
        }

        if (assetKind == LauncherAssetKind.Icon && !IsSupportedIconUpload(file.FileName))
        {
            return BadRequest(new { error = "The app icon must be a .ico or an image (PNG, JPG, WebP, GIF, BMP)." });
        }

        await using var stream = file.OpenReadStream();
        var config = await _portal.SaveGlobalAssetAsync(assetKind, file.FileName, stream, cancellationToken);
        // A new global background/logo becomes the default for every stack without an override, so re-push.
        if (assetKind is LauncherAssetKind.Background or LauncherAssetKind.Logo)
        {
            await RepushBrandingAsync(cancellationToken);
        }
        return Ok(config);
    }

    // ===== Global news =====

    [HttpGet("news")]
    public async Task<ActionResult<IReadOnlyList<LauncherNewsItemDto>>> GetGlobalNews(CancellationToken cancellationToken) =>
        Ok(await _portal.GetGlobalNewsAsync(includeDrafts: true, cancellationToken));

    [HttpPut("news")]
    public async Task<ActionResult<IReadOnlyList<LauncherNewsItemDto>>> SaveGlobalNews(
        [FromBody] List<LauncherNewsItemDto> items, CancellationToken cancellationToken)
    {
        var saved = await _portal.SaveGlobalNewsAsync(items ?? new List<LauncherNewsItemDto>(), cancellationToken);

        // A global article is an announcement for every stack, so push the published feed out to each
        // launcher-visible stack's own news store. Best-effort: never fail the save on a broadcast hiccup.
        try
        {
            await _portal.BroadcastGlobalNewsAsync(cancellationToken);
            // The broadcast updated each stack's manager-side news store; push the feeds to the stacks'
            // client containers so the change reaches players' launchers.
            await _registry.RebuildAndPushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast global news to stacks after save.");
        }

        return Ok(saved);
    }

    /// <summary>
    /// Re-broadcasts the published global news feed to every launcher-visible stack on demand (the same
    /// push that happens automatically on save). Returns per-stack results for the admin UI.
    /// </summary>
    [HttpPost("news/broadcast")]
    public async Task<ActionResult<GlobalNewsBroadcastResult>> BroadcastGlobalNews(CancellationToken cancellationToken)
    {
        var result = await _portal.BroadcastGlobalNewsAsync(cancellationToken);
        await RepushBrandingAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("news/{itemId}/image")]
    public async Task<ActionResult<IReadOnlyList<LauncherNewsItemDto>>> UploadGlobalNewsImage(
        string itemId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded." });
        }

        await using var stream = file.OpenReadStream();
        return Ok(await _portal.SaveGlobalNewsImageAsync(itemId, file.FileName, stream, cancellationToken));
    }

    // ===== Per-stack news =====

    [HttpGet("stacks/{stackId}/news")]
    public async Task<ActionResult<IReadOnlyList<LauncherNewsItemDto>>> GetStackNews(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _portal.GetStackNewsAsync(stackId, includeDrafts: true, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("stacks/{stackId}/news")]
    public async Task<ActionResult<IReadOnlyList<LauncherNewsItemDto>>> SaveStackNews(
        string stackId, [FromBody] List<LauncherNewsItemDto> items, CancellationToken cancellationToken)
    {
        try
        {
            var saved = await _portal.SaveStackNewsAsync(stackId, items ?? new List<LauncherNewsItemDto>(), cancellationToken);
            // Push the updated feed to the stack's client container so it reaches players' launchers.
            await RepushBrandingAsync(cancellationToken);
            return Ok(saved);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("stacks/{stackId}/news/{itemId}/image")]
    public async Task<ActionResult<IReadOnlyList<LauncherNewsItemDto>>> UploadStackNewsImage(
        string stackId, string itemId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _portal.SaveStackNewsImageAsync(stackId, itemId, file.FileName, stream, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("stacks/{stackId}/profile")]
    public async Task<ActionResult<LauncherProfileConfigDto>> GetProfile(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _portal.GetProfileAsync(stackId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("stacks/{stackId}/profile")]
    public async Task<ActionResult<LauncherProfileConfigDto>> SaveProfile(
        string stackId, [FromBody] LauncherProfileConfigDto profile, CancellationToken cancellationToken)
    {
        profile.StackId = stackId;
        try
        {
            var saved = await _portal.SaveProfileAsync(profile, cancellationToken);

            // Saving is the commit point for all profile edits: visibility/display/realmlist/template
            // changes the replicated registry, and this is also where any staged wallpaper/logo upload (or
            // removal) is finally pushed to each stack's client container. RebuildAndPushAsync does both.
            // Best-effort: never fail the save on a push hiccup.
            try
            {
                await _registry.RebuildAndPushAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to re-push registry after profile edit for stack {StackId}.", stackId);
            }

            return Ok(saved);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("stacks/{stackId}/profile/assets/{kind}")]
    public async Task<ActionResult<LauncherProfileConfigDto>> UploadProfileAsset(
        string stackId, string kind, IFormFile file, CancellationToken cancellationToken)
    {
        if (!TryParseKind(kind, out var assetKind))
        {
            return BadRequest(new { error = $"Unknown asset kind '{kind}'." });
        }

        if (assetKind == LauncherAssetKind.Icon)
        {
            return BadRequest(new { error = "The app icon is a global-only setting and cannot be set per profile." });
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var profile = await _portal.SaveProfileAssetAsync(stackId, assetKind, file.FileName, stream, cancellationToken);
            // Stage only: the upload is stored on the manager (and shown in the admin preview) but is NOT
            // pushed to the stack's client container yet. It ships to players when the admin clicks
            // "Save profile" (which re-pushes the registry + branding). This keeps unsaved edits private.
            return Ok(profile);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Removes a stack's uploaded wallpaper/logo override so the launcher shows the global theme's
    /// asset again. The app icon is a global-only setting, so it cannot be removed per stack.
    /// </summary>
    [HttpDelete("stacks/{stackId}/profile/assets/{kind}")]
    public async Task<ActionResult<LauncherProfileConfigDto>> DeleteProfileAsset(
        string stackId, string kind, CancellationToken cancellationToken)
    {
        if (!TryParseKind(kind, out var assetKind))
        {
            return BadRequest(new { error = $"Unknown asset kind '{kind}'." });
        }

        if (assetKind == LauncherAssetKind.Icon)
        {
            return BadRequest(new { error = "The app icon is a global-only setting and cannot be set per profile." });
        }

        try
        {
            var profile = await _portal.DeleteProfileAssetAsync(stackId, assetKind, cancellationToken);
            // Stage only: the override is removed on the manager (preview reverts to the global theme) but
            // the change reaches players only when the admin clicks "Save profile" (which re-pushes the
            // registry + branding), consistent with uploads deferring to save.
            return Ok(profile);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Rebuilds and re-pushes the replicated registry (which also refreshes each stack's branding images
    /// in its own client container). Best-effort: a push hiccup never fails the admin edit.
    /// </summary>
    private async Task RepushBrandingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _registry.RebuildAndPushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-push launcher branding to stacks.");
        }
    }

    private static readonly string[] IconUploadExtensions =
        [".ico", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];

    private static bool IsSupportedIconUpload(string fileName) =>
        IconUploadExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseKind(string kind, out LauncherAssetKind assetKind)
    {
        switch (kind?.ToLowerInvariant())
        {
            case "background":
                assetKind = LauncherAssetKind.Background;
                return true;
            case "logo":
                assetKind = LauncherAssetKind.Logo;
                return true;
            case "icon":
                assetKind = LauncherAssetKind.Icon;
                return true;
            default:
                assetKind = default;
                return false;
        }
    }
}
