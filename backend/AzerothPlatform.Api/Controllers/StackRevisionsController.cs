using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzerothPlatform.Api.Controllers;

/// <summary>
/// Manages a stack's point-in-time revisions (snapshots) under <c>api/stacks/{stackId}/revisions</c>:
/// list, create manually, restore (rollback databases, config, and checkpoint images), and delete.
/// </summary>
[Authorize]
[ApiController]
[Route("api/stacks/{stackId}/revisions")]
public class StackRevisionsController : ControllerBase
{
    private readonly IRevisionService _revisions;

    public StackRevisionsController(IRevisionService revisions)
    {
        _revisions = revisions;
    }

    /// <summary>Lists a stack's revisions, newest first.</summary>
    [HttpGet]
    public Task<IActionResult> List(string stackId, CancellationToken cancellationToken)
        => StackFileApi.Execute(() => _revisions.ListAsync(stackId, cancellationToken));

    /// <summary>Creates a manual snapshot of the stack's databases + config.</summary>
    [HttpPost]
    public Task<IActionResult> Create(string stackId, CancellationToken cancellationToken)
        => StackFileApi.Execute(() => _revisions.CreateAsync(stackId, "manual", cancellationToken));

    /// <summary>Restores databases, config, and checkpoint images from a revision. Restart the stack afterwards.</summary>
    [HttpPost("{revisionId}/restore")]
    public Task<IActionResult> Restore(string stackId, string revisionId, CancellationToken cancellationToken)
        => StackFileApi.Execute(async () =>
        {
            await _revisions.RestoreAsync(stackId, revisionId, cancellationToken);
            return new { restored = true };
        });

    /// <summary>Deletes a revision and its dump files.</summary>
    [HttpDelete("{revisionId}")]
    public Task<IActionResult> Delete(string stackId, string revisionId, CancellationToken cancellationToken)
        => StackFileApi.Execute(async () =>
        {
            await _revisions.DeleteAsync(stackId, revisionId, cancellationToken);
            return new { deleted = true };
        });
}
