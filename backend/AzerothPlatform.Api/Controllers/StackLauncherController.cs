using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Per-stack launcher endpoints that remain on the manager as control-plane / admin-dashboard support:
/// news + branding artwork the dashboard renders, admin-triggered container rescan/force-verify, and
/// the Config.wtf template editor. The player download path (config/manifest/files) belongs to each
/// stack's own client-server container and must not be added here.
/// </summary>
[ApiController]
[Route("api/stacks/{stackId}/launcher")]
public class StackLauncherController : ControllerBase
{
    private readonly IStackLauncherService _launcher;
    private readonly ILauncherPortalService _portal;

    public StackLauncherController(IStackLauncherService launcher, ILauncherPortalService portal)
    {
        _launcher = launcher;
        _portal = portal;
    }

    /// <summary>Serves a per-profile branding asset (background/logo) or the profile's news XML.</summary>
    [AllowAnonymous]
    [HttpGet("profile-asset/{asset}")]
    public async Task<IActionResult> GetProfileAsset(string stackId, string asset, CancellationToken cancellationToken)
    {
        try
        {
            await _launcher.EnsureLauncherVisibleAsync(stackId, cancellationToken);
            var resolved = await _portal.ResolveProfileAssetAsync(stackId, asset, cancellationToken);
            if (resolved is null)
            {
                return NotFound();
            }

            return PhysicalFile(resolved.Value.Path, resolved.Value.ContentType);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>This profile's news articles (rich HTML with cover images).</summary>
    [AllowAnonymous]
    [HttpGet("news")]
    public async Task<ActionResult<IReadOnlyList<LauncherNewsItemDto>>> GetNews(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            await _launcher.EnsureLauncherVisibleAsync(stackId, cancellationToken);
            return Ok(await _portal.GetStackNewsAsync(stackId, cancellationToken: cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Serves a per-profile news article's cover image.</summary>
    [AllowAnonymous]
    [HttpGet("news-image/{itemId}")]
    public async Task<IActionResult> GetNewsImage(string stackId, string itemId, CancellationToken cancellationToken)
    {
        try
        {
            await _launcher.EnsureLauncherVisibleAsync(stackId, cancellationToken);
            var resolved = await _portal.ResolveStackNewsImageAsync(stackId, itemId, cancellationToken);
            return resolved is null ? NotFound() : PhysicalFile(resolved.Value.Path, resolved.Value.ContentType);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("rescan")]
    public async Task<ActionResult<ClientManifest>> Rescan(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _launcher.RescanAsync(stackId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Forces every launcher pointed at this stack to full-verify (re-hash) all client files on its next
    /// check, even when the manifest content is unchanged (e.g. after a same-size Config.wtf edit).
    /// </summary>
    [Authorize]
    [HttpPost("force-verify")]
    public async Task<ActionResult<ClientManifest>> ForceVerify(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _launcher.ForceVerifyAsync(stackId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Clears the client-server hash cache, re-hashes every distributable file, rebuilds the manifest
    /// (with corrected base/managed groups), and bumps the verify token so launchers full-sync.
    /// </summary>
    [Authorize]
    [HttpPost("rebuild-manifest")]
    public async Task<ActionResult<ClientManifestRebuildResultDto>> RebuildManifest(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _launcher.RebuildManifestAsync(stackId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns this stack's editable <c>WTF/Config.wtf</c> settings template (placeholders intact). This
    /// template seeds Config.wtf on a player's first install; later launches only patch the realmlist.
    /// </summary>
    [Authorize]
    [HttpGet("config-template")]
    public async Task<ActionResult<ClientConfigTemplateDto>> GetConfigTemplate(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(new ClientConfigTemplateDto { Content = await _launcher.GetConfigTemplateAsync(stackId, cancellationToken) });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Overwrites this stack's <c>WTF/Config.wtf</c> settings template.</summary>
    [Authorize]
    [HttpPut("config-template")]
    public async Task<IActionResult> SaveConfigTemplate(
        string stackId, [FromBody] ClientConfigTemplateDto body, CancellationToken cancellationToken)
    {
        try
        {
            await _launcher.SaveConfigTemplateAsync(stackId, body?.Content ?? string.Empty, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
