using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Server Wide Progression custom setup: bootstrap, patch validation, and sync from
/// <c>mod-individual-progression</c> plus Azeroth-Platform-Progression.
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/server-wide-progression")]
public class ServerWideProgressionController : ControllerBase
{
    private readonly IServerWideProgressionService _progression;

    public ServerWideProgressionController(IServerWideProgressionService progression)
    {
        _progression = progression;
    }

    [HttpPost("bootstrap")]
    public Task<IActionResult> Bootstrap(string stackId, CancellationToken cancellationToken)
        => Execute(() => _progression.BootstrapAsync(stackId, cancellationToken));

    /// <summary>Creates any missing Server Wide Progression patch template folders without resetting config.</summary>
    [HttpPost("recreate-missing-patches")]
    public Task<IActionResult> RecreateMissingPatches(string stackId, CancellationToken cancellationToken)
        => Execute(() => _progression.RecreateMissingPatchesAsync(stackId, cancellationToken));

    /// <summary>
    /// Verifies patch templates and config overrides. When Server Wide Progression is bootstrapped and
    /// Azeroth-Platform-Progression is synced, validates folder structure against the repository as well.
    /// </summary>
    [HttpPost("validate-patches")]
    public Task<IActionResult> ValidatePatches(string stackId, CancellationToken cancellationToken)
        => Execute(() => _progression.ValidatePatchesAsync(stackId, cancellationToken));

    [HttpGet("sync/status")]
    public Task<IActionResult> GetSyncStatus(string stackId, CancellationToken cancellationToken)
        => Execute(() => _progression.GetSyncStatusAsync(stackId, cancellationToken));

    [HttpPost("sync/run")]
    public Task<IActionResult> RunSync(string stackId, CancellationToken cancellationToken)
        => Execute(() => _progression.RunSyncAsync(stackId, cancellationToken));

    [HttpPost("sync/resolve-optional")]
    public Task<IActionResult> ResolveOptionalFiles(
        string stackId,
        [FromBody] ResolveOptionalFilesRequest request,
        CancellationToken cancellationToken)
        => Execute(() => _progression.ResolveOptionalFilesAsync(stackId, request, cancellationToken));

    [HttpGet("sync/ignored-files")]
    public Task<IActionResult> GetIgnoredFiles(string stackId, CancellationToken cancellationToken)
        => Execute(() => _progression.GetIgnoredFilesAsync(stackId, cancellationToken));

    [HttpPost("sync/reprompt")]
    public Task<IActionResult> RepromptIgnoredFile(
        string stackId,
        [FromQuery] string source,
        CancellationToken cancellationToken)
        => Execute(() => _progression.RepromptIgnoredFileAsync(stackId, source, cancellationToken));

    private static async Task<IActionResult> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            return new OkObjectResult(result);
        }
        catch (KeyNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return new NotFoundObjectResult(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return new ObjectResult(new { error = ex.Message }) { StatusCode = 500 };
        }
    }
}
