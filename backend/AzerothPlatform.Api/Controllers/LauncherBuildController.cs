using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Triggers compilation of the desktop launcher and serves the produced exe for download. Building/
/// status is admin-only; the download stays anonymous so the admin dashboard's plain <c>&lt;a&gt;</c>
/// download link works. Players fetch the launcher from their own stack's client container, not here.
/// </summary>
[Authorize]
[ApiController]
[Route("api/launcher-build")]
public class LauncherBuildController : ControllerBase
{
    private readonly ILauncherBuildService _build;

    public LauncherBuildController(ILauncherBuildService build)
    {
        _build = build;
    }

    [HttpPost]
    public async Task<ActionResult<LauncherBuildStatusDto>> Build(
        [FromBody] LauncherBuildRequestDto? request, CancellationToken cancellationToken)
    {
        var part = Enum.TryParse<LauncherVersionPart>(request?.Part, ignoreCase: true, out var parsed)
            ? parsed
            : LauncherVersionPart.Patch;
        return Ok(await _build.StartBuildAsync(part, cancellationToken));
    }

    [HttpGet("status")]
    public async Task<ActionResult<LauncherBuildStatusDto>> Status(CancellationToken cancellationToken) =>
        Ok(await _build.GetStatusAsync(cancellationToken));

    /// <summary>
    /// Pings every client-enabled stack for the launcher version it currently serves and compares it
    /// against the manager's most recently built version, so the admin can confirm the build propagated.
    /// </summary>
    [HttpGet("stack-versions")]
    public async Task<ActionResult<LauncherPropagationDto>> StackVersions(CancellationToken cancellationToken) =>
        Ok(await _build.GetStackVersionsAsync(cancellationToken));

    /// <summary>Re-pushes the current build to a single stack that is stale or missed the last build.</summary>
    [HttpPost("stacks/{stackId}/resend")]
    public async Task<ActionResult<LauncherStackVersionDto>> Resend(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _build.ResendToStackAsync(stackId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [AllowAnonymous]
    [HttpGet("download")]
    public IActionResult Download()
    {
        var exe = _build.GetExecutablePath();
        if (exe is null)
        {
            return NotFound(new { error = "No launcher build is available yet. Build the launcher first." });
        }

        return PhysicalFile(exe, "application/octet-stream", Path.GetFileName(exe), enableRangeProcessing: true);
    }
}
