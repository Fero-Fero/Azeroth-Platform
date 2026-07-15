using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Admin control-plane endpoints for the assets the manager's own admin dashboard renders (news cover
/// images + style-template artwork for the launcher preview).
///
/// The player-facing launcher path (profiles, login, config, manifest, files, branding) and the
/// game-client distribution no longer live here: each stack's own client-server container serves those
/// directly, so the manager is never in the player path.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class LauncherController : ControllerBase
{
    private readonly ILauncherPortalService _portal;

    public LauncherController(ILauncherPortalService portal)
    {
        _portal = portal;
    }

    /// <summary>Serves a global news article's cover image (rendered in the admin news editor preview).</summary>
    [AllowAnonymous]
    [HttpGet("news-image/{itemId}")]
    public IActionResult GetNewsImage(string itemId)
    {
        var resolved = _portal.ResolveGlobalNewsImage(itemId);
        return resolved is null ? NotFound() : PhysicalFile(resolved.Value.Path, resolved.Value.ContentType);
    }

    /// <summary>Serves a style template's shipped artwork (used by the admin launcher preview).</summary>
    [AllowAnonymous]
    [HttpGet("templates/{templateId}/{asset}")]
    public IActionResult GetTemplateAsset(string templateId, string asset)
    {
        var resolved = _portal.ResolveTemplateAsset(templateId, asset);
        if (resolved is null)
        {
            return NotFound();
        }

        return PhysicalFile(resolved.Value.Path, resolved.Value.ContentType);
    }
}
